using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeerOnQ.Domain.Errors;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Shared.Contracts.V1;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

[Collection("Linux desktop")]
public sealed class LinuxCloudEnrollmentTests
{
    [LinuxCloudFact]
    public async Task ActualLinuxCompositionEnrollsWithKeyringProofAndSuppliesCloudAttestationToSignaling()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var endpoint = $"http://127.0.0.1:{port}/";
        var environment = new Dictionary<string, string?>
        {
            ["PEERONQ_API_BASE_URL"] = endpoint,
            ["PEERONQ_PRESENCE_URL"] = endpoint,
            ["PEERONQ_SIGNALING_URL"] = $"ws://127.0.0.1:{port}/ws",
            ["PEERONQ_UPDATES_BASE_URL"] = "https://updates.example.test/",
            ["PEERONQ_DOWNLOADS_BASE_URL"] = endpoint,
            ["PEERONQ_DIAGNOSTICS_BASE_URL"] = endpoint,
            ["PEERONQ_DEPLOYMENT_ENVIRONMENT"] = "Development",
            ["PEERONQ_REGION"] = "linux-test",
            ["PEERONQ_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "peeronq-linux-cloud-" + Guid.NewGuid().ToString("N")),
        };
        var previous = environment.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
        try
        {
            var fixture = ServeProofAndObserveAttestationAsync(server, deadline.Token);
            await using var services = await LinuxAppServices.CreateAsync(deadline.Token);
            // The fixture deliberately refuses registration after observing the real client proof.
            // This proves composition without pretending to be a production attestation issuer.
            await Assert.ThrowsAsync<SignalingAuthenticationException>(() => services.ConnectAsync(deadline.Token));
            await fixture.WaitAsync(deadline.Token);
            Assert.True(services.Identity.PublicIdServerAssigned);
            Assert.Equal("246-802-468-024", services.Identity.PublicId.Display);
        }
        finally
        {
            await deadline.CancelAsync();
            server.Stop();
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task ServeProofAndObserveAttestationAsync(HttpListener server, CancellationToken cancellationToken)
    {
        var registration = await server.GetContextAsync().WaitAsync(cancellationToken);
        Assert.Equal("/v1/devices/register", registration.Request.Url!.AbsolutePath);
        var request = await ReadJsonAsync<DeviceRegistrationRequestV1>(registration, cancellationToken);
        Assert.Equal(PlatformKindV1.Linux, request.Platform);
        Assert.Equal(InstallChannelV1.Development, request.InstallChannel);
        Assert.Matches(@"^\d+\.\d+\.\d+$", request.AppVersion);
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds());
        var canonical = $"peeronq-device-auth-v1\nissued_at={expires.AddSeconds(-90).ToUnixTimeSeconds()}\nexpires_at={expires.ToUnixTimeSeconds()}";
        await WriteJsonAsync(registration, new DeviceRegistrationChallengeV1("synthetic-cloud-challenge", canonical, expires), cancellationToken);

        var authentication = await server.GetContextAsync().WaitAsync(cancellationToken);
        Assert.Equal("/v1/devices/authenticate", authentication.Request.Url!.AbsolutePath);
        var proof = await ReadJsonAsync<DeviceAuthenticationRequestV1>(authentication, cancellationToken);
        Assert.Equal(request.InstallationId, proof.InstallationId);
        Assert.Equal(request.PublicKeySpkiBase64, proof.PublicKeySpkiBase64);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKeySpkiBase64), out _);
        Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(canonical), Convert.FromBase64String(proof.SignatureBase64), HashAlgorithmName.SHA256));
        const string attestation = "synthetic-memory-only-cloud-attestation";
        await WriteJsonAsync(authentication, new DeviceAuthenticationResultV1("synthetic-memory-only-access-token",
            DateTimeOffset.UtcNow.AddMinutes(10), Guid.NewGuid(), request.InstallationId, "246-802-468-024",
            attestation, DateTimeOffset.UtcNow.AddMinutes(5)), cancellationToken);

        var installation = await server.GetContextAsync().WaitAsync(cancellationToken);
        Assert.Equal("/v1/installations/register", installation.Request.Url!.AbsolutePath);
        Assert.Equal("Bearer synthetic-memory-only-access-token", installation.Request.Headers["Authorization"]);
        var installed = await ReadJsonAsync<InstallationRegistrationRequestV1>(installation, cancellationToken);
        Assert.Equal(PlatformKindV1.Linux, installed.Platform);
        Assert.Equal(request.InstallationId, installed.InstallationId);
        await WriteJsonAsync(installation, new InstallationRegistrationResultV1(request.InstallationId,
            DateTimeOffset.UtcNow, true, false, null), cancellationToken);

        var signaling = await server.GetContextAsync().WaitAsync(cancellationToken);
        Assert.Equal("/ws", signaling.Request.Url!.AbsolutePath);
        using var socket = (await signaling.AcceptWebSocketAsync(null)).WebSocket;
        var hello = Assert.IsType<HelloMessage>(await ReceiveAsync(socket, cancellationToken));
        Assert.Equal(PeerOnQId.Parse("246-802-468-024").Value, hello.DeviceId);
        Assert.Equal(request.AppVersion, hello.ClientVersion);
        Assert.Equal(PeerOnQClientPlatforms.Linux, hello.ClientCapabilities!.Platform);
        const string nonce = "synthetic-signaling-proof-challenge";
        await socket.SendAsync(SignalingCodec.Encode(new ChallengeMessage { Nonce = nonce, ExpiresAt = expires }).AsMemory(),
            WebSocketMessageType.Text, true, cancellationToken);
        var bound = Assert.IsType<RegisterMessage>(await ReceiveAsync(socket, cancellationToken));
        Assert.Equal(hello.DeviceId, bound.DeviceId);
        Assert.Equal(attestation, bound.CloudAttestation);
        Assert.Equal(request.PublicKeySpkiBase64, bound.PublicKey);
        Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(nonce), Convert.FromBase64String(bound.Proof), HashAlgorithmName.SHA256));
        await socket.SendAsync(SignalingCodec.Encode(new ErrorMessage
        {
            Code = SignalingErrorCodes.InvalidAttestation,
            Message = "The synthetic fixture does not issue production attestations.",
        }).AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpListenerContext context, CancellationToken cancellationToken) =>
        (await JsonSerializer.DeserializeAsync<T>(context.Request.InputStream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken))!;

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    private static async Task<SignalingMessage> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[SignalingCodec.MaxFrameBytes];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes.AsMemory(length), cancellationToken);
            length += result.Count;
        } while (!result.EndOfMessage && length < bytes.Length);
        Assert.True(result.EndOfMessage);
        return SignalingCodec.Decode(bytes.AsSpan(0, length));
    }

    private sealed class LinuxCloudFactAttribute : FactAttribute
    {
        public LinuxCloudFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PEERONQ_LINUX_NATIVE_TEST") != "1")
                Skip = "Requires a real Linux Secret Service; enable PEERONQ_LINUX_NATIVE_TEST=1.";
        }
    }
}

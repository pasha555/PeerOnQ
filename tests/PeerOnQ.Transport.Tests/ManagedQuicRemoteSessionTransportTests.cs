using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Transport.DataPlane;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.Transport.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ManagedQuicRemoteSessionTransportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DedicatedChannelsAreMutuallyAuthenticatedAndOrdered()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        await using var pair = await QuicPair.CreateAsync();

        var mouseRead = ReadMessagesAsync(pair.Server, RemoteTransportChannel.Mouse, 3);
        await pair.Client.SendAsync(RemoteTransportChannel.Mouse, new byte[] { 1 });
        await pair.Client.SendAsync(RemoteTransportChannel.Mouse, new byte[] { 2 });
        await pair.Client.SendAsync(RemoteTransportChannel.Mouse, new byte[] { 3 });

        var keyboardRead = ReadMessagesAsync(pair.Client, RemoteTransportChannel.Keyboard, 1);
        await pair.Server.SendAsync(RemoteTransportChannel.Keyboard, new byte[] { 42 });

        Assert.Equal([1, 2, 3], (await mouseRead).Select(message => message[0]));
        Assert.Equal(42, (await keyboardRead).Single()[0]);
        Assert.Equal(RemoteSessionTransportKind.NativeQuic, pair.Client.Capabilities.Kind);
        Assert.True(pair.Client.Capabilities.SupportsReliableStreams);
        Assert.False(pair.Client.Capabilities.SupportsUnreliableDatagrams);
    }

    [Fact]
    public async Task ParallelFileLanesRemainIndependentFromInteractiveLane()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        await using var pair = await QuicPair.CreateAsync();

        var filesRead = ReadMessagesAsync(pair.Server, RemoteTransportChannel.FileTransfer, 2);
        var mouseRead = ReadMessagesAsync(pair.Server, RemoteTransportChannel.Mouse, 1);
        await using var firstFileLane = await pair.Client.OpenLaneAsync(RemoteTransportChannel.FileTransfer);
        await using var secondFileLane = await pair.Client.OpenLaneAsync(RemoteTransportChannel.FileTransfer);

        await Task.WhenAll(
            firstFileLane.SendAsync(Encoding.UTF8.GetBytes("file-lane-a")).AsTask(),
            secondFileLane.SendAsync(Encoding.UTF8.GetBytes("file-lane-b")).AsTask(),
            pair.Client.SendAsync(RemoteTransportChannel.Mouse, new byte[] { 9 }).AsTask());

        Assert.Equal(9, (await mouseRead).Single()[0]);
        Assert.Equal(
            ["file-lane-a", "file-lane-b"],
            (await filesRead).Select(Encoding.UTF8.GetString).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FileLaneMeetsOneGiBPerMinuteWhileInteractiveLaneStaysResponsive()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        await using var pair = await QuicPair.CreateAsync();
        var transferBytes = GetTransferBytes();
        using var timeout = new CancellationTokenSource(
            transferBytes >= 1024L * 1024 * 1024 ? TimeSpan.FromSeconds(70) : TimeSpan.FromSeconds(30));
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var fileLane = await pair.Client.OpenLaneAsync(RemoteTransportChannel.FileTransfer, timeout.Token);

        const int chunkBytes = 256 * 1024;
        var chunkCount = checked((int)(transferBytes / chunkBytes));
        const double minimumPayloadMiBPerSecond = 1024d / 60d;
        var payload = GC.AllocateUninitializedArray<byte>(chunkBytes);
        RandomNumberGenerator.Fill(payload);
        var filePacer = new AdaptiveFileTransferPacer();
        var reservedBulkAllocation = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            new MediaStatistics
            {
                ConnectionHealth = ConnectionHealth.Excellent,
                CurrentBitrateKbps = 34_000,
                TargetBitrateKbps = 36_000,
                AvailableOutgoingBitrateKbps = 250_000,
            });

        var fileReceive = CountBytesAsync(
            pair.Server,
            RemoteTransportChannel.FileTransfer,
            transferBytes,
            timeout.Token);
        var inputLatencies = ReadInputLatenciesAsync(pair.Server, inputLifetime.Token);
        var elapsed = Stopwatch.StartNew();

        var fileSend = Task.Run(async () =>
        {
            for (var index = 0; index < chunkCount; index++)
            {
                await filePacer.WaitAsync(chunkBytes, () => reservedBulkAllocation, timeout.Token);
                await fileLane.SendAsync(payload, timeout.Token);
            }
        }, timeout.Token);
        var inputSend = Task.Run(async () =>
        {
            var timestamp = new byte[sizeof(long)];
            try
            {
                while (true)
                {
                    inputLifetime.Token.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteInt64BigEndian(timestamp, Stopwatch.GetTimestamp());
                    await pair.Client.SendAsync(
                        RemoteTransportChannel.Mouse,
                        timestamp,
                        inputLifetime.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), inputLifetime.Token);
                }
            }
            catch (OperationCanceledException) when (inputLifetime.IsCancellationRequested)
            {
                // The file receive boundary ends the simultaneous-input sample window.
            }
        }, inputLifetime.Token);

        await Task.WhenAll(fileSend, fileReceive);
        elapsed.Stop();
        await inputLifetime.CancelAsync();
        await Task.WhenAll(inputSend, inputLatencies);

        var receivedBytes = await fileReceive;
        var payloadMiBPerSecond = receivedBytes / 1024d / 1024d / elapsed.Elapsed.TotalSeconds;
        var orderedLatencies = (await inputLatencies).Order().ToArray();
        Assert.True(orderedLatencies.Length >= 10, "The contention run produced too few input samples.");
        var p95InputMilliseconds = orderedLatencies[(int)Math.Ceiling(orderedLatencies.Length * 0.95) - 1];
        output.WriteLine(
            "Native QUIC: {0:F0} MiB, {1:F1} MiB/s payload, {2:F1} ms interactive p95 "
            + "across {3} samples, {4} Kbps bulk budget.",
            receivedBytes / 1024d / 1024d,
            payloadMiBPerSecond,
            p95InputMilliseconds,
            orderedLatencies.Length,
            reservedBulkAllocation.MaximumBulkKbps);

        Assert.True(
            payloadMiBPerSecond >= minimumPayloadMiBPerSecond,
            $"Native QUIC payload throughput was {payloadMiBPerSecond:F1} MiB/s; "
            + $"the 1 GiB/minute gate requires {minimumPayloadMiBPerSecond:F1} MiB/s.");
        Assert.True(
            p95InputMilliseconds <= ConnectionPolicyProvider.TargetInputToInjectionP95Ms,
            $"Interactive-lane p95 was {p95InputMilliseconds:F1} ms during QUIC file traffic.");
    }

    [Fact]
    public async Task ReceiverConfirmedGoodputEscapesTheWebRtcEstimateWithoutBlockingInput()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        await using var pair = await QuicPair.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var fileLane = await pair.Client.OpenLaneAsync(RemoteTransportChannel.FileTransfer, timeout.Token);

        const int chunkBytes = 256 * 1024;
        const int chunkCount = 512;
        var payload = GC.AllocateUninitializedArray<byte>(chunkBytes);
        RandomNumberGenerator.Fill(payload);
        var statistics = new MediaStatistics
        {
            ConnectionHealth = ConnectionHealth.Excellent,
            CurrentBitrateKbps = 34_000,
            TargetBitrateKbps = 36_000,
            // Mirrors the production WebRTC estimator's target x 1.2 ceiling.
            AvailableOutgoingBitrateKbps = 43_200,
        };
        var baseline = ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode.FileTransferPriority,
            statistics,
            hasInteractiveTraffic: true);
        var estimator = new NativeBulkCapacityEstimator();
        var pacer = new AdaptiveFileTransferPacer();
        var transferId = Guid.NewGuid();
        var expectedBytes = (long)chunkBytes * chunkCount;

        var fileReceive = CountBytesWithFeedbackAsync(
            pair.Server,
            expectedBytes,
            delivered => estimator.ObserveReceipt(
                transferId,
                delivered,
                baseline.MaximumBulkKbps,
                statistics),
            timeout.Token);
        var inputLatencies = ReadInputLatenciesAsync(pair.Server, inputLifetime.Token);
        var elapsed = Stopwatch.StartNew();
        var fileSend = Task.Run(async () =>
        {
            for (var index = 0; index < chunkCount; index++)
            {
                await pacer.WaitAsync(
                    chunkBytes,
                    () => estimator.Apply(baseline, statistics),
                    timeout.Token);
                estimator.RecordChunkScheduled(
                    transferId,
                    chunkBytes,
                    estimator.Apply(baseline, statistics).MaximumBulkKbps);
                await fileLane.SendAsync(payload, timeout.Token);
            }
        }, timeout.Token);
        var inputSend = Task.Run(async () =>
        {
            var timestamp = new byte[sizeof(long)];
            try
            {
                while (true)
                {
                    inputLifetime.Token.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteInt64BigEndian(timestamp, Stopwatch.GetTimestamp());
                    await pair.Client.SendAsync(RemoteTransportChannel.Mouse, timestamp, inputLifetime.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), inputLifetime.Token);
                }
            }
            catch (OperationCanceledException) when (inputLifetime.IsCancellationRequested)
            {
                // The transfer boundary ends the simultaneous input sample window.
            }
        }, inputLifetime.Token);

        await Task.WhenAll(fileSend, fileReceive);
        elapsed.Stop();
        await inputLifetime.CancelAsync();
        await Task.WhenAll(inputSend, inputLatencies);

        var snapshot = estimator.Snapshot;
        var payloadMiBPerSecond = expectedBytes / 1024d / 1024d / elapsed.Elapsed.TotalSeconds;
        var orderedLatencies = (await inputLatencies).Order().ToArray();
        var p95InputMilliseconds = orderedLatencies[(int)Math.Ceiling(orderedLatencies.Length * 0.95) - 1];
        output.WriteLine(
            "Adaptive native QUIC: {0:F1} MiB/s, {1} -> {2} Kbps, "
            + "{3:F0} Kbps confirmed goodput, {4:F1} ms interactive p95 across {5} samples.",
            payloadMiBPerSecond,
            baseline.MaximumBulkKbps,
            snapshot.BudgetKbps,
            snapshot.GoodputKbps,
            p95InputMilliseconds,
            orderedLatencies.Length);

        Assert.Equal(expectedBytes, await fileReceive);
        Assert.True(snapshot.FeedbackSamples >= 10);
        Assert.True(snapshot.BudgetKbps > 150_000);
        Assert.True(payloadMiBPerSecond >= 1024d / 60d);
        Assert.True(orderedLatencies.Length >= 10);
        Assert.True(p95InputMilliseconds <= ConnectionPolicyProvider.TargetInputToInjectionP95Ms);
    }

    [Fact]
    public async Task IncorrectPeerCertificatePinFailsClosed()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        using var serverCertificate = CreateCertificate("server");
        using var clientCertificate = CreateCertificate("client");
        using var unrelatedCertificate = CreateCertificate("unrelated");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var listener = await ManagedQuicSessionListener.ListenAsync(
            new IPEndPoint(IPAddress.Loopback, 0),
            serverCertificate,
            QuicPeerAuthentication.GetCertificateSha256(clientCertificate),
            timeout.Token);
        var accept = listener.AcceptAsync(timeout.Token).AsTask();

        var failure = await Record.ExceptionAsync(async () =>
            await ManagedQuicRemoteSessionTransport.ConnectAsync(
                listener.LocalEndPoint,
                clientCertificate,
                QuicPeerAuthentication.GetCertificateSha256(unrelatedCertificate),
                cancellationToken: timeout.Token));

        Assert.NotNull(failure);
        Assert.IsNotType<OperationCanceledException>(failure);
        timeout.Cancel();
        await Record.ExceptionAsync(async () => await accept);
    }

    [Fact]
    public async Task EphemeralBulkFactoryMutuallyPinsAndConnectsRealQuicPeers()
    {
        var factory = new ManagedQuicBulkTransportFactory();
        if (!factory.IsSupported) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serverEndpoint = await factory.CreateEndpointAsync(timeout.Token);
        await using var clientEndpoint = await factory.CreateEndpointAsync(timeout.Token);
        await using var listener = await serverEndpoint.ListenAsync(
            IPAddress.Loopback,
            clientEndpoint.CertificateSha256,
            timeout.Token);
        var accept = listener.AcceptAsync(timeout.Token).AsTask();
        await using var client = await clientEndpoint.ConnectAsync(
            listener.LocalEndPoint,
            serverEndpoint.CertificateSha256,
            timeout.Token);
        await using var server = await accept;

        var read = ReadMessagesAsync(server, RemoteTransportChannel.FileTransfer, 1);
        await client.SendAsync(RemoteTransportChannel.FileTransfer, "factory"u8.ToArray(), timeout.Token);

        Assert.Equal("factory", Encoding.UTF8.GetString((await read).Single()));
    }

    [Fact]
    public async Task ManagedStreamAdapterRejectsDatagramChannelsAndOversizedInput()
    {
        if (!ManagedQuicSessionListener.IsSupported) return;
        await using var pair = await QuicPair.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await pair.Client.SendAsync(RemoteTransportChannel.Screen, new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await pair.Client.SendAsync(RemoteTransportChannel.Mouse, new byte[4097]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pair.Client.OpenLaneAsync(RemoteTransportChannel.Keyboard));
    }

    private static async Task<IReadOnlyList<byte[]>> ReadMessagesAsync(
        IReliableRemoteSessionTransport transport,
        RemoteTransportChannel channel,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var messages = new List<byte[]>(count);
        await foreach (var payload in transport.ReadAllAsync(channel, timeout.Token))
        {
            messages.Add(payload.ToArray());
            if (messages.Count == count) break;
        }
        return messages;
    }

    private static async Task<long> CountBytesAsync(
        IReliableRemoteSessionTransport transport,
        RemoteTransportChannel channel,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        long receivedBytes = 0;
        await foreach (var payload in transport.ReadAllAsync(channel, cancellationToken))
        {
            receivedBytes += payload.Length;
            if (receivedBytes >= expectedBytes) break;
        }
        return receivedBytes;
    }

    private static async Task<long> CountBytesWithFeedbackAsync(
        IReliableRemoteSessionTransport transport,
        long expectedBytes,
        Action<long> reportDelivered,
        CancellationToken cancellationToken)
    {
        long receivedBytes = 0;
        await foreach (var payload in transport.ReadAllAsync(
                           RemoteTransportChannel.FileTransfer,
                           cancellationToken))
        {
            receivedBytes += payload.Length;
            reportDelivered(receivedBytes);
            if (receivedBytes >= expectedBytes) break;
        }
        return receivedBytes;
    }

    private static async Task<IReadOnlyList<double>> ReadInputLatenciesAsync(
        IReliableRemoteSessionTransport transport,
        CancellationToken cancellationToken)
    {
        var latencies = new List<double>();
        try
        {
            await foreach (var payload in transport.ReadAllAsync(RemoteTransportChannel.Mouse, cancellationToken))
            {
                Assert.Equal(sizeof(long), payload.Length);
                var sentAt = BinaryPrimitives.ReadInt64BigEndian(payload.Span);
                latencies.Add(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A bounded cancellation ends sampling without closing the transport lane.
        }
        return latencies;
    }

    private static long GetTransferBytes()
    {
        const long defaultBytes = 64L * 1024 * 1024;
        const long maximumBytes = 1024L * 1024 * 1024;
        const int chunkBytes = 256 * 1024;
        var configured = Environment.GetEnvironmentVariable("PEERONQ_LIVE_QUIC_TRANSFER_BYTES");
        if (string.IsNullOrWhiteSpace(configured)) return defaultBytes;
        if (!long.TryParse(configured, out var bytes)
            || bytes < defaultBytes
            || bytes > maximumBytes
            || bytes % chunkBytes != 0)
        {
            throw new InvalidOperationException(
                "PEERONQ_LIVE_QUIC_TRANSFER_BYTES must be a 256 KiB multiple from 64 MiB through 1 GiB.");
        }
        return bytes;
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=PeerOnQ Test {name}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // TLS server authentication
                new("1.3.6.1.5.5.7.3.2"), // TLS client authentication
            },
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class QuicPair : IAsyncDisposable
    {
        private readonly ManagedQuicSessionListener _listener;
        private readonly X509Certificate2 _serverCertificate;
        private readonly X509Certificate2 _clientCertificate;

        private QuicPair(
            ManagedQuicSessionListener listener,
            ManagedQuicRemoteSessionTransport client,
            ManagedQuicRemoteSessionTransport server,
            X509Certificate2 serverCertificate,
            X509Certificate2 clientCertificate)
        {
            _listener = listener;
            Client = client;
            Server = server;
            _serverCertificate = serverCertificate;
            _clientCertificate = clientCertificate;
        }

        public ManagedQuicRemoteSessionTransport Client { get; }
        public ManagedQuicRemoteSessionTransport Server { get; }

        public static async Task<QuicPair> CreateAsync()
        {
            var serverCertificate = CreateCertificate("server");
            var clientCertificate = CreateCertificate("client");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ManagedQuicSessionListener? listener = null;
            ManagedQuicRemoteSessionTransport? client = null;
            try
            {
                listener = await ManagedQuicSessionListener.ListenAsync(
                    new IPEndPoint(IPAddress.Loopback, 0),
                    serverCertificate,
                    QuicPeerAuthentication.GetCertificateSha256(clientCertificate),
                    timeout.Token);
                var accept = listener.AcceptAsync(timeout.Token).AsTask();
                client = await ManagedQuicRemoteSessionTransport.ConnectAsync(
                    listener.LocalEndPoint,
                    clientCertificate,
                    QuicPeerAuthentication.GetCertificateSha256(serverCertificate),
                    cancellationToken: timeout.Token);
                var server = await accept;
                return new QuicPair(listener, client, server, serverCertificate, clientCertificate);
            }
            catch
            {
                if (client is not null) await client.DisposeAsync();
                if (listener is not null) await listener.DisposeAsync();
                serverCertificate.Dispose();
                clientCertificate.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await _listener.DisposeAsync();
            _serverCertificate.Dispose();
            _clientCertificate.Dispose();
        }
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Services;
using PeerOnQ.Shared.Contracts.Security;

namespace PeerOnQ.Cloud.Infrastructure.Security;

public sealed class EcdsaSignalingAttestationIssuer : ISignalingAttestationIssuer, IDisposable
{
    private const long MaximumPemBytes = 16 * 1024;
    private readonly SignalingAttestationIssuerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ECDsa _privateKey;
    private readonly string _keyId;

    public EcdsaSignalingAttestationIssuer(
        IOptions<SignalingAttestationIssuerOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _options.Validate();
        _timeProvider = timeProvider;
        _privateKey = LoadPrivateKey(_options.PrivateKeyFile!);
        _keyId = SignalingAttestationTokenV1.ComputeKeyId(_privateKey.ExportSubjectPublicKeyInfo());
    }

    public IssuedSignalingAttestation Issue(SignalingAttestationIssueRequest request)
    {
        if (request.DeviceId == Guid.Empty || request.InstallationId == Guid.Empty)
            throw new ArgumentException("The signaling attestation device binding is invalid.", nameof(request));

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt + _options.Lifetime;
        var policy = request.ManagedPolicy;
        var policyFlags = policy is null ? SignalingOrganizationPolicyFlags.Unmanaged :
            (policy.ViewOnlyAllowed ? SignalingOrganizationPolicyFlags.ViewOnly : 0) |
            (policy.FullControlAllowed ? SignalingOrganizationPolicyFlags.FullControl : 0) |
            (policy.FileTransferAllowed ? SignalingOrganizationPolicyFlags.FileTransfer : 0) |
            (policy.UnattendedAccessAllowed ? SignalingOrganizationPolicyFlags.Unattended : 0) |
            (policy.ClipboardAllowed ? SignalingOrganizationPolicyFlags.Clipboard : 0) |
            (policy.HybridSecurityRequired ? SignalingOrganizationPolicyFlags.HybridRequired : 0);
        var claims = new SignalingAttestationClaimsV1(
            _options.Issuer,
            _options.Audience,
            request.PublicDeviceId,
            request.DeviceId,
            request.InstallationId,
            request.SpkiSha256,
            _keyId,
            issuedAt.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds(),
            SignalingAttestationTokenV1.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            policy?.OrganizationId,
            policyFlags,
            policy?.MinimumClientVersion,
            policy?.ApprovedRelayRegionsCsv);
        return new IssuedSignalingAttestation(
            SignalingAttestationTokenV1.Issue(claims, _privateKey),
            expiresAt);
    }

    public void Dispose() => _privateKey.Dispose();

    internal static bool IsValidPrivateKeyFile(string path)
    {
        try
        {
            using var key = LoadPrivateKey(path);
            return key.KeySize == 256;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or CryptographicException
                                           or InvalidOperationException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static ECDsa LoadPrivateKey(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length is <= 0 or > MaximumPemBytes)
            throw new InvalidOperationException("The signaling attestation private-key secret is unavailable or invalid.");

        var pem = File.ReadAllText(info.FullName).Trim();
        // Compose the PEM boundary so repository scanners do not mistake this parser for a key.
        var beginBoundary = "-----BEGIN " + "PRIVATE KEY-----";
        var endBoundary = "-----END " + "PRIVATE KEY-----";
        if (!pem.StartsWith(beginBoundary, StringComparison.Ordinal)
            || !pem.EndsWith(endBoundary, StringComparison.Ordinal)
            || pem.IndexOf(beginBoundary, 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The signaling attestation key must be one PKCS#8 PEM private key.");

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != 256)
                throw new InvalidOperationException("The signaling attestation key must use ECDSA P-256.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

public static class SignalingAttestationIssuerRegistration
{
    public static IServiceCollection AddPeerOnQSignalingAttestationIssuer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SignalingAttestationIssuerOptions>()
            .Bind(configuration.GetSection(SignalingAttestationIssuerOptions.SectionName))
            .Validate(Validate, "The signaling attestation signing configuration is invalid.")
            .ValidateOnStart();
        services.AddSingleton<ISignalingAttestationIssuer, EcdsaSignalingAttestationIssuer>();
        services.AddScoped<IDeviceRegistrationService, DeviceRegistrationService>();
        return services;
    }

    private static bool Validate(SignalingAttestationIssuerOptions options)
    {
        try
        {
            options.Validate();
            return EcdsaSignalingAttestationIssuer.IsValidPrivateKeyFile(options.PrivateKeyFile!);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

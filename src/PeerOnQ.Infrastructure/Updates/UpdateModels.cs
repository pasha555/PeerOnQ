using System.Runtime.InteropServices;
using PeerOnQ.Infrastructure.Compatibility;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Infrastructure.Updates;

public enum UpdateChannel
{
    Stable = 0,
    Beta = 1,
}

public enum UpdateCheckStatus
{
    NoUpdate = 0,
    Available = 1,
    Required = 2,
    DeferredByRollout = 3,
    Rejected = 4,
}

public enum UpdateRejectionReason
{
    None = 0,
    InvalidEnvelope = 1,
    InvalidSignature = 2,
    UntrustedKey = 3,
    ExpiredManifest = 4,
    ManifestNotYetValid = 5,
    WrongProduct = 6,
    WrongArchitecture = 7,
    WrongChannel = 8,
    InvalidVersion = 9,
    UnauthorizedDowngrade = 10,
    BelowSecurityFloor = 11,
    InvalidPackage = 12,
    InsecureTransport = 13,
    UntrustedDownloadHost = 14,
    HashMismatch = 15,
    InvalidAuthenticodeSignature = 16,
    WrongPublisher = 17,
    DownloadTooLarge = 18,
    NetworkFailure = 19,
    UnexpectedRedirect = 20,
}

public sealed record UpdatePackageDescriptor
{
    public required string Architecture { get; init; }
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public string InstallerType { get; init; } = "msi";
}

public sealed record UpdateManifest
{
    public required int SchemaVersion { get; init; }
    public required string ProductId { get; init; }
    public required string Version { get; init; }
    public required string MinimumSupportedVersion { get; init; }
    public required UpdateChannel Channel { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required int RolloutPercentage { get; init; }
    public required string RolloutSeed { get; init; }
    public bool SecurityEmergency { get; init; }
    public required IReadOnlyList<UpdatePackageDescriptor> Packages { get; init; }
}

public sealed record SignedUpdateEnvelope
{
    public required int SchemaVersion { get; init; }
    public required string KeyId { get; init; }
    public required string Payload { get; init; }
    public required string Signature { get; init; }
}

public sealed record VerifiedUpdate(
    UpdateManifest Manifest,
    UpdatePackageDescriptor Package,
    Version Version,
    Version MinimumSupportedVersion);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    VerifiedUpdate? Update = null,
    UpdateRejectionReason RejectionReason = UpdateRejectionReason.None,
    string? Detail = null);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public sealed record StagedUpdate(VerifiedUpdate Update, string InstallerPath);

public sealed record FileSignatureVerification(bool IsTrusted, string? CertificateSha256, string? Failure);

public interface IAuthenticodeVerifier
{
    FileSignatureVerification Verify(string filePath);
}

public interface IUpdateEventSink
{
    ValueTask EnqueueAsync(ClientUpdateEventV1 updateEvent, CancellationToken cancellationToken = default);
}

public sealed record UpdateClientOptions
{
    public const string DefaultProductId = "com.peeronq.desktop";
    public static string LegacyProductId => LegacyBrandCompatibility.DesktopProductId;

    public required Uri ManifestUri { get; init; }
    public required string PublicKeySpkiBase64 { get; init; }
    public required string KeyId { get; init; }
    public required IReadOnlySet<string> AllowedPublisherCertificateSha256 { get; init; }
    public required string UpdateDirectory { get; init; }
    public required string DeviceRolloutId { get; init; }
    public string ProductId { get; init; } = DefaultProductId;
    public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);
    public UpdateChannel Channel { get; init; } = UpdateChannel.Beta;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };
    public long MaximumPackageBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumManifestBytes { get; init; } = 128 * 1024;
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class UpdateSecurityException(UpdateRejectionReason reason, string message) : Exception(message)
{
    public UpdateRejectionReason Reason { get; } = reason;
}

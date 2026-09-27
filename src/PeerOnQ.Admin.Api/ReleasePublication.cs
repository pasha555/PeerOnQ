using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Admin.Api;

public sealed class ReleasePublicationOptions
{
    public const string SectionName = "PeerOnQ:ReleasePublication";
    public const long MaximumPackageBytesLimit = 96L * 1024 * 1024;
    public const long MaximumRequestBodyBytes = 100L * 1024 * 1024;
    public const int MaximumManifestBytes = 128 * 1024;

    public bool Enabled { get; set; }
    public string StorageDirectory { get; set; } = string.Empty;
    public Uri? PublicBaseUrl { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;
    public long MaximumPackageBytes { get; set; } = MaximumPackageBytesLimit;
}

internal sealed class ReleasePublicationOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<ReleasePublicationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReleasePublicationOptions options)
    {
        if (!options.Enabled)
        {
            return environment.IsProduction() || environment.IsStaging()
                ? ValidateOptionsResult.Fail("Release publication must be explicitly configured outside development.")
                : ValidateOptionsResult.Success;
        }

        if (!Path.IsPathFullyQualified(options.StorageDirectory))
            return ValidateOptionsResult.Fail("Release publication storage must be an absolute path.");
        if (options.PublicBaseUrl is not { IsAbsoluteUri: true } baseUrl
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment)
            || baseUrl.AbsolutePath != "/")
            return ValidateOptionsResult.Fail("Release publication requires a root HTTPS public base URL.");
        if (string.IsNullOrWhiteSpace(options.KeyId) || options.KeyId.Length > 128)
            return ValidateOptionsResult.Fail("Release publication requires a bounded signing key ID.");
        if (options.MaximumPackageBytes is <= 0 or > ReleasePublicationOptions.MaximumPackageBytesLimit)
            return ValidateOptionsResult.Fail("The release publication package limit is invalid.");

        try
        {
            var key = Convert.FromBase64String(options.PublicKeySpkiBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || ecdsa.KeySize != 256)
                return ValidateOptionsResult.Fail("The release publication key must be ECDSA P-256 SPKI.");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return ValidateOptionsResult.Fail("The release publication public key is invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class ReleaseUploadGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(TimeSpan.Zero, cancellationToken)) return null;
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}

internal sealed record VerifiedReleaseManifest(
    string Version,
    string MinimumSupportedVersion,
    InstallChannel Channel,
    ArchitectureKind Architecture,
    int RolloutPercentage,
    Uri ArtifactUri,
    string ArtifactFileName,
    long ArtifactSizeBytes,
    string ArtifactSha256,
    string SignedManifestDigest,
    byte[] EnvelopeBytes);

internal sealed partial class ReleasePublicationVerifier(
    IOptions<ReleasePublicationOptions> configuredOptions,
    TimeProvider timeProvider)
{
    private readonly ReleasePublicationOptions _options = configuredOptions.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [GeneratedRegex(@"^PeerOnQ-(?<version>[0-9]+(?:\.[0-9]+){2,3})-(?<architecture>x64|arm64)\.msi$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();

    public VerifiedReleaseManifest Verify(ReadOnlySpan<byte> envelopeBytes)
    {
        EnsureEnabled();
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > ReleasePublicationOptions.MaximumManifestBytes)
            Reject("release_manifest_invalid", "The signed release manifest has an invalid size.");

        SignedEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeBytes, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Invalid("release_manifest_invalid", "The signed release manifest is malformed.");
        }

        if (envelope.SchemaVersion != 1 || !FixedEquals(envelope.KeyId, _options.KeyId))
            Reject("release_signing_key_untrusted", "The release manifest signing key is not trusted.");

        byte[] payload;
        byte[] signature;
        byte[] publicKey;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
            publicKey = Convert.FromBase64String(_options.PublicKeySpkiBase64);
        }
        catch (FormatException)
        {
            throw Invalid("release_manifest_invalid", "The signed release manifest contains invalid base64 data.");
        }

        if (payload.Length is 0 or > 64 * 1024 || signature.Length is 0 or > 256 || publicKey.Length is 0 or > 1024)
            Reject("release_manifest_invalid", "The signed release manifest contains invalid field sizes.");

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || !ecdsa.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                Reject("release_signature_invalid", "The release manifest signature is invalid.");
        }
        catch (CryptographicException)
        {
            throw Invalid("release_signing_key_untrusted", "The configured release verification key is invalid.");
        }

        SignedPayload manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SignedPayload>(payload, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Invalid("release_manifest_invalid", "The signed release payload is malformed.");
        }

        if (manifest.SchemaVersion != 1 || manifest.ProductId != "com.peeronq.desktop")
            Reject("release_product_invalid", "The release manifest belongs to another product or schema.");
        if (manifest.Channel is < 0 or > 1 || manifest.RolloutPercentage is < 0 or > 100)
            Reject("release_manifest_invalid", "The release channel or rollout is invalid.");
        if (string.IsNullOrWhiteSpace(manifest.RolloutSeed) || manifest.RolloutSeed.Length > 128)
            Reject("release_manifest_invalid", "The release rollout seed is invalid.");
        if (manifest.Packages is not { Count: 1 })
            Reject("release_manifest_invalid", "Each uploaded manifest must contain exactly one package.");

        var now = timeProvider.GetUtcNow();
        if (manifest.IssuedAt > now.AddMinutes(5)
            || manifest.ExpiresAt <= now
            || manifest.ExpiresAt <= manifest.IssuedAt
            || manifest.ExpiresAt - manifest.IssuedAt > TimeSpan.FromDays(30))
            Reject("release_manifest_expired", "The release manifest validity window is invalid or expired.");

        if (!TryNormalizedVersion(manifest.Version, out var version)
            || !TryNormalizedVersion(manifest.MinimumSupportedVersion, out var minimum)
            || version < minimum)
            Reject("release_version_invalid", "The release version or minimum supported version is invalid.");

        var package = manifest.Packages[0];
        var architecture = package.Architecture switch
        {
            "x64" => ArchitectureKind.X64,
            "arm64" => ArchitectureKind.Arm64,
            _ => throw Invalid("release_architecture_invalid", "Only Windows x64 and arm64 releases are supported."),
        };
        if (package.InstallerType != "msi"
            || package.SizeBytes is <= 0
            || package.SizeBytes > _options.MaximumPackageBytes
            || package.Sha256.Length != 64
            || package.Sha256.Any(character => !Uri.IsHexDigit(character)))
            Reject("release_package_invalid", "The signed package descriptor is invalid.");

        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var artifactUri)
            || artifactUri.Scheme != Uri.UriSchemeHttps
            || !SameOrigin(artifactUri, _options.PublicBaseUrl!)
            || !string.IsNullOrEmpty(artifactUri.UserInfo)
            || !string.IsNullOrEmpty(artifactUri.Query)
            || !string.IsNullOrEmpty(artifactUri.Fragment)
            || artifactUri.AbsolutePath.Contains('%', StringComparison.Ordinal))
            Reject("release_artifact_url_invalid", "The signed package URL is outside the configured release origin.");

        var fileName = Path.GetFileName(artifactUri.AbsolutePath);
        var match = PackageNamePattern().Match(fileName);
        var channelName = manifest.Channel == 0 ? "stable" : "beta";
        var expectedPath = $"/{channelName}/{package.Architecture}/{fileName}";
        if (!match.Success
            || match.Groups["architecture"].Value != package.Architecture
            || artifactUri.AbsolutePath != expectedPath
            || !TryNormalizedVersion(match.Groups["version"].Value, out var fileVersion)
            || fileVersion != version)
            Reject("release_artifact_url_invalid", "The signed package URL path does not match its release metadata.");

        var digest = Convert.ToHexString(SHA256.HashData(envelopeBytes)).ToLowerInvariant();
        return new VerifiedReleaseManifest(
            manifest.Version,
            manifest.MinimumSupportedVersion,
            manifest.Channel == 0 ? InstallChannel.Stable : InstallChannel.Beta,
            architecture,
            manifest.RolloutPercentage,
            artifactUri,
            fileName,
            package.SizeBytes,
            package.Sha256.ToLowerInvariant(),
            digest,
            envelopeBytes.ToArray());
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "release_publication_disabled", "Release publication is not configured.");
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool TryNormalizedVersion(string value, out Version normalized)
    {
        normalized = new Version();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !Version.TryParse(value, out var parsed)) return false;
        normalized = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static ApiProblemException Invalid(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);

    [DoesNotReturn]
    private static void Reject(string code, string detail) => throw Invalid(code, detail);

    private sealed record SignedEnvelope
    {
        public required int SchemaVersion { get; init; }
        public required string KeyId { get; init; }
        public required string Payload { get; init; }
        public required string Signature { get; init; }
    }

    private sealed record SignedPayload
    {
        public required int SchemaVersion { get; init; }
        public required string ProductId { get; init; }
        public required string Version { get; init; }
        public required string MinimumSupportedVersion { get; init; }
        public required int Channel { get; init; }
        public required DateTimeOffset IssuedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required int RolloutPercentage { get; init; }
        public required string RolloutSeed { get; init; }
        public bool SecurityEmergency { get; init; }
        public required IReadOnlyList<SignedPackage> Packages { get; init; }
    }

    private sealed record SignedPackage
    {
        public required string Architecture { get; init; }
        public required string Url { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
        public required string InstallerType { get; init; }
    }
}

internal sealed class ReleasePublicationService(
    CloudDbContext db,
    ReleasePublicationVerifier verifier,
    IOptions<ReleasePublicationOptions> configuredOptions,
    AdminAuditWriter audit,
    AdminRequestContext requestContext,
    TimeProvider timeProvider)
{
    private readonly ReleasePublicationOptions _options = configuredOptions.Value;

    public async Task<AdminReleasePublicationV1> PublishAsync(
        IFormFile manifestFile,
        IFormFile packageFile,
        string reason,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var manifestBytes = await ReadManifestAsync(manifestFile, cancellationToken);
        var verified = verifier.Verify(manifestBytes);
        if (packageFile.Length != verified.ArtifactSizeBytes)
            throw Invalid("release_package_size_mismatch", "The uploaded package size does not match the signed manifest.");
        if (await db.AppReleases.AnyAsync(value => value.Version == verified.Version
                && value.Channel == verified.Channel
                && value.Architecture == verified.Architecture, cancellationToken))
            throw new ApiProblemException(StatusCodes.Status409Conflict, "release_exists", "This version, channel, and architecture already exists.");

        var storageRoot = EnsureStorageRoot();
        var stagingDirectory = Path.Combine(storageRoot, ".staging");
        EnsureControlledDirectory(stagingDirectory);
        var stagedPackage = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.partial");
        var channel = verified.Channel.ToString().ToLowerInvariant();
        var architecture = verified.Architecture.ToString().ToLowerInvariant();
        var finalDirectory = Path.Combine(storageRoot, channel, architecture);
        EnsureControlledDirectory(finalDirectory);
        var finalPackage = Path.Combine(finalDirectory, verified.ArtifactFileName);
        var finalManifest = Path.Combine(finalDirectory, "manifest.json");
        if (File.Exists(finalPackage))
            throw new ApiProblemException(StatusCodes.Status409Conflict, "release_artifact_exists", "The release artifact already exists.");

        var previousManifest = File.Exists(finalManifest)
            ? await File.ReadAllBytesAsync(finalManifest, cancellationToken)
            : null;
        var packagePublished = false;
        var manifestPublished = false;
        try
        {
            await CopyAndVerifyPackageAsync(packageFile, stagedPackage, verified, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var release = new AppRelease(
                Guid.NewGuid(),
                verified.Version,
                verified.Channel,
                verified.Architecture,
                now,
                verified.MinimumSupportedVersion,
                verified.MinimumSupportedVersion,
                verified.RolloutPercentage,
                verified.SignedManifestDigest,
                verified.ArtifactUri,
                verified.ArtifactSizeBytes,
                verified.ArtifactSha256);
            release.Deactivate();
            db.AppReleases.Add(release);
            await db.SaveChangesAsync(cancellationToken);

            File.Move(stagedPackage, finalPackage, overwrite: false);
            packagePublished = true;
            MakePublicReadOnly(finalPackage);
            await PublishManifestAsync(finalManifest, verified.EnvelopeBytes, cancellationToken);
            manifestPublished = true;

            release.Activate();
            audit.Add(
                context,
                AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
                "release.publish",
                "AppRelease",
                release.Id.ToString("N"),
                AuditResult.Succeeded,
                reason.Trim(),
                requestContext,
                now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Response(release);
        }
        catch
        {
            if (manifestPublished) await RestoreManifestAsync(finalManifest, previousManifest);
            if (packagePublished) DeleteFile(finalPackage);
            throw;
        }
        finally
        {
            DeleteFile(stagedPackage);
        }
    }

    public async Task<AdminReleasePublicationV1> ReplaceManifestAsync(
        Guid releaseId,
        AdminReleaseManifestRequestV1 request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(request.Reason);
        var bytes = Encoding.UTF8.GetBytes(request.SignedManifest ?? string.Empty);
        var verified = verifier.Verify(bytes);
        var release = await db.AppReleases.SingleOrDefaultAsync(value => value.Id == releaseId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (release.Version != verified.Version
            || release.Channel != verified.Channel
            || release.Architecture != verified.Architecture
            || release.MinimumSupportedVersion != verified.MinimumSupportedVersion
            || release.ArtifactUri != verified.ArtifactUri.AbsoluteUri
            || release.ArtifactSizeBytes != verified.ArtifactSizeBytes
            || !release.ArtifactSha256.Equals(verified.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("release_manifest_mismatch", "The replacement manifest changes immutable release metadata.");

        var storageRoot = EnsureStorageRoot();
        var finalManifest = Path.Combine(
            storageRoot,
            release.Channel.ToString().ToLowerInvariant(),
            release.Architecture.ToString().ToLowerInvariant(),
            "manifest.json");
        if (!File.Exists(finalManifest))
            throw new ApiProblemException(StatusCodes.Status409Conflict, "release_manifest_missing", "The current published manifest is missing.");
        var previousManifest = await File.ReadAllBytesAsync(finalManifest, cancellationToken);
        var replaced = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await PublishManifestAsync(finalManifest, verified.EnvelopeBytes, cancellationToken);
            replaced = true;
            release.ReplaceSignedManifest(verified.SignedManifestDigest, verified.RolloutPercentage);
            var now = timeProvider.GetUtcNow();
            audit.Add(
                context,
                AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
                "release.manifest.replace",
                "AppRelease",
                release.Id.ToString("N"),
                AuditResult.Succeeded,
                request.Reason.Trim(),
                requestContext,
                now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Response(release);
        }
        catch
        {
            if (replaced) await RestoreManifestAsync(finalManifest, previousManifest);
            throw;
        }
    }

    private string EnsureStorageRoot()
    {
        if (!_options.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "release_publication_disabled", "Release publication is not configured.");
        var root = Path.GetFullPath(_options.StorageDirectory);
        EnsureControlledDirectory(root);
        return root;
    }

    private static void EnsureControlledDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Release publication storage cannot be a reparse point.");
    }

    private async Task CopyAndVerifyPackageAsync(
        IFormFile package,
        string destination,
        VerifiedReleaseManifest verified,
        CancellationToken cancellationToken)
    {
        await using var source = package.OpenReadStream();
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > verified.ArtifactSizeBytes || total > _options.MaximumPackageBytes)
                throw Invalid("release_package_size_mismatch", "The uploaded package exceeds the signed size.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
        var actualHash = hash.GetHashAndReset();
        var expectedHash = Convert.FromHexString(verified.ArtifactSha256);
        if (total != verified.ArtifactSizeBytes
            || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw Invalid("release_package_integrity_mismatch", "The uploaded package does not match the signed SHA-256 digest.");
    }

    private static async Task<byte[]> ReadManifestAsync(IFormFile manifest, CancellationToken cancellationToken)
    {
        if (manifest.Length is <= 0 or > ReleasePublicationOptions.MaximumManifestBytes)
            throw Invalid("release_manifest_invalid", "The signed release manifest has an invalid size.");
        await using var stream = manifest.OpenReadStream();
        using var buffer = new MemoryStream((int)manifest.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != manifest.Length) throw Invalid("release_manifest_invalid", "The signed release manifest changed during upload.");
        return buffer.ToArray();
    }

    private static async Task PublishManifestAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            MakePublicReadOnly(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            DeleteFile(temporary);
        }
    }

    private static async Task RestoreManifestAsync(string destination, byte[]? previous)
    {
        if (previous is null)
        {
            DeleteFile(destination);
            return;
        }
        await PublishManifestAsync(destination, previous, CancellationToken.None);
    }

    private static void MakePublicReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 3 or > 512)
            throw new ArgumentException("A bounded audit reason is required.", nameof(reason));
    }

    private static AdminReleasePublicationV1 Response(AppRelease release) => new(
        release.Id,
        release.Version,
        Enum.Parse<InstallChannelV1>(release.Channel.ToString()),
        Enum.Parse<ArchitectureKindV1>(release.Architecture.ToString()),
        release.RolloutPercentage,
        release.PublishedAtUtc);

    private static ApiProblemException Invalid(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);
}

using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api;

public sealed class WebsitePublicationOptions
{
    public const string SectionName = "PeerOnQ:WebsitePublication";
    public const long MaximumArchiveBytesLimit = 96L * 1024 * 1024;
    public const long MaximumRequestBodyBytes = 100L * 1024 * 1024;
    public const int MaximumManifestBytes = 128 * 1024;
    public const long MaximumExpandedBytes = 192L * 1024 * 1024;
    public const int MaximumEntries = 4096;

    public bool Enabled { get; set; }
    public string StorageDirectory { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeySpkiBase64 { get; set; } = string.Empty;
    public long MaximumArchiveBytes { get; set; } = MaximumArchiveBytesLimit;
}

internal sealed class WebsitePublicationOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<WebsitePublicationOptions>
{
    public ValidateOptionsResult Validate(string? name, WebsitePublicationOptions options)
    {
        if (!options.Enabled)
        {
            return environment.IsProduction() || environment.IsStaging()
                ? ValidateOptionsResult.Fail("Website publication must be explicitly configured outside development.")
                : ValidateOptionsResult.Success;
        }

        if (!Path.IsPathFullyQualified(options.StorageDirectory))
            return ValidateOptionsResult.Fail("Website publication storage must be an absolute path.");
        if (string.IsNullOrWhiteSpace(options.KeyId) || options.KeyId.Length > 128)
            return ValidateOptionsResult.Fail("Website publication requires a bounded signing key ID.");
        if (options.MaximumArchiveBytes is <= 0 or > WebsitePublicationOptions.MaximumArchiveBytesLimit)
            return ValidateOptionsResult.Fail("The website archive limit is invalid.");

        try
        {
            var key = Convert.FromBase64String(options.PublicKeySpkiBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || ecdsa.KeySize != 256)
                return ValidateOptionsResult.Fail("The website publication key must be ECDSA P-256 SPKI.");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return ValidateOptionsResult.Fail("The website publication public key is invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class WebsiteUploadGate
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

public sealed record WebsiteReleaseV1(
    string Version,
    DateTimeOffset PublishedAtUtc,
    string ArchiveSha256,
    long ArchiveSizeBytes,
    bool IsActive,
    bool IsRollbackCandidate);

public sealed record WebsiteReleaseActivationRequestV1(string Reason);

internal sealed record VerifiedWebsiteManifest(
    string Version,
    string ArchiveFileName,
    string ArchiveSha256,
    long ArchiveSizeBytes,
    string EntryPoint,
    string ManifestDigest,
    byte[] EnvelopeBytes);

internal sealed partial class WebsitePublicationVerifier(
    IOptions<WebsitePublicationOptions> configuredOptions,
    TimeProvider timeProvider)
{
    private readonly WebsitePublicationOptions _options = configuredOptions.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [GeneratedRegex(@"^PeerOnQ-website-(?<version>[0-9]+(?:\.[0-9]+){2,3})\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveNamePattern();

    public VerifiedWebsiteManifest Verify(ReadOnlySpan<byte> envelopeBytes)
    {
        EnsureEnabled();
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > WebsitePublicationOptions.MaximumManifestBytes)
            Reject("website_manifest_invalid", "The signed website manifest has an invalid size.");

        SignedEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeBytes, JsonOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Invalid("website_manifest_invalid", "The signed website manifest is malformed.");
        }

        if (envelope.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.KeyId) || !FixedEquals(envelope.KeyId, _options.KeyId))
            Reject("website_signing_key_untrusted", "The website manifest signing key is not trusted.");
        if (string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Signature))
            Reject("website_manifest_invalid", "The signed website manifest is incomplete.");

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
            throw Invalid("website_manifest_invalid", "The signed website manifest contains invalid base64 data.");
        }

        if (payload.Length is 0 or > 64 * 1024 || signature.Length is 0 or > 256 || publicKey.Length is 0 or > 1024)
            Reject("website_manifest_invalid", "The signed website manifest contains invalid field sizes.");

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || !ecdsa.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                Reject("website_signature_invalid", "The website manifest signature is invalid.");
        }
        catch (CryptographicException)
        {
            throw Invalid("website_signing_key_untrusted", "The configured website verification key is invalid.");
        }

        SignedPayload manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SignedPayload>(payload, JsonOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Invalid("website_manifest_invalid", "The signed website payload is malformed.");
        }

        var now = timeProvider.GetUtcNow();
        if (manifest.SchemaVersion != 1 || manifest.ProductId != "com.peeronq.website")
            Reject("website_product_invalid", "The website manifest belongs to another product or schema.");
        if (manifest.IssuedAt > now.AddMinutes(5)
            || manifest.ExpiresAt <= now
            || manifest.ExpiresAt <= manifest.IssuedAt
            || manifest.ExpiresAt - manifest.IssuedAt > TimeSpan.FromDays(30))
            Reject("website_manifest_expired", "The website manifest validity window is invalid or expired.");
        if (!TryNormalizedVersion(manifest.Version, out var version))
            Reject("website_version_invalid", "The website version is invalid.");
        if (manifest.ArchiveSizeBytes is <= 0 || manifest.ArchiveSizeBytes > _options.MaximumArchiveBytes
            || string.IsNullOrWhiteSpace(manifest.ArchiveSha256) || manifest.ArchiveSha256.Length != 64
            || manifest.ArchiveSha256.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(manifest.ArchiveFileName))
            Reject("website_archive_invalid", "The signed website archive descriptor is invalid.");
        if (manifest.EntryPoint != "index.html")
            Reject("website_entry_point_invalid", "The website entry point must be index.html.");

        var match = ArchiveNamePattern().Match(manifest.ArchiveFileName);
        if (!match.Success || !TryNormalizedVersion(match.Groups["version"].Value, out var archiveVersion) || archiveVersion != version)
            Reject("website_archive_invalid", "The website archive name does not match its version.");

        return new VerifiedWebsiteManifest(
            manifest.Version,
            manifest.ArchiveFileName,
            manifest.ArchiveSha256.ToLowerInvariant(),
            manifest.ArchiveSizeBytes,
            manifest.EntryPoint,
            Convert.ToHexString(SHA256.HashData(envelopeBytes)).ToLowerInvariant(),
            envelopeBytes.ToArray());
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "website_publication_disabled", "Website publication is not configured.");
    }

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
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static ApiProblemException Invalid(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);

    [DoesNotReturn]
    private static void Reject(string code, string detail) => throw Invalid(code, detail);

    private sealed record SignedEnvelope(int SchemaVersion, string KeyId, string Payload, string Signature);
    private sealed record SignedPayload(
        int SchemaVersion,
        string ProductId,
        string Version,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string ArchiveFileName,
        string ArchiveSha256,
        long ArchiveSizeBytes,
        string EntryPoint);
}

internal sealed partial class WebsitePublicationService(
    CloudDbContext db,
    WebsitePublicationVerifier verifier,
    IOptions<WebsitePublicationOptions> configuredOptions,
    AdminAuditWriter audit,
    AdminRequestContext requestContext,
    TimeProvider timeProvider)
{
    private const string DownloadsUiCompatibilityEntry = "peeronq-downloads-ui-v1.html";
    private readonly WebsitePublicationOptions _options = configuredOptions.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".css", ".js", ".json", ".txt", ".svg", ".ico", ".png", ".webp", ".avif",
        ".woff", ".woff2", ".ttf", ".webmanifest",
    };

    public IReadOnlyList<WebsiteReleaseV1> List()
    {
        var root = EnsureStorageRoot();
        var active = ReadLinkVersion(root, "current");
        var previous = ReadLinkVersion(root, "previous");
        var releases = new List<WebsiteReleaseV1>();
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "releases")))
        {
            var metadataPath = Path.Combine(directory, ".peeronq-release.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<WebsiteReleaseMetadata>(File.ReadAllBytes(metadataPath), JsonOptions);
                if (metadata is null || Path.GetFileName(directory) != metadata.Version) continue;
                releases.Add(new WebsiteReleaseV1(
                    metadata.Version,
                    metadata.PublishedAtUtc,
                    metadata.ArchiveSha256,
                    metadata.ArchiveSizeBytes,
                    metadata.Version == active,
                    metadata.Version == previous));
            }
            catch (JsonException) { }
        }
        return releases.OrderByDescending(value => value.PublishedAtUtc).ToArray();
    }

    public async Task<WebsiteReleaseV1> PublishAsync(
        IFormFile manifestFile,
        IFormFile archiveFile,
        string reason,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var manifestBytes = await ReadManifestAsync(manifestFile, cancellationToken);
        var verified = verifier.Verify(manifestBytes);
        if (archiveFile.Length != verified.ArchiveSizeBytes || archiveFile.FileName != verified.ArchiveFileName)
            throw Invalid("website_archive_mismatch", "The uploaded archive name or size does not match the signed manifest.");

        var root = EnsureStorageRoot();
        var releasesRoot = Path.Combine(root, "releases");
        var finalDirectory = Path.Combine(releasesRoot, verified.Version);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new ApiProblemException(StatusCodes.Status409Conflict, "website_release_exists", "This website version already exists.");

        var stagingDirectory = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N"));
        var stagedArchive = stagingDirectory + ".zip.partial";
        Directory.CreateDirectory(stagingDirectory);
        var previousActive = ReadLinkVersion(root, "current");
        var previousRollback = ReadLinkVersion(root, "previous");
        var moved = false;
        try
        {
            await CopyAndVerifyArchiveAsync(archiveFile, stagedArchive, verified, cancellationToken);
            try
            {
                ExtractVerifiedArchive(stagedArchive, stagingDirectory);
            }
            catch (InvalidDataException)
            {
                throw Invalid("website_archive_invalid", "The website ZIP structure or compressed data is invalid.");
            }
            var now = timeProvider.GetUtcNow();
            await WriteMetadataAsync(stagingDirectory, verified, now, cancellationToken);
            MakeTreePublicReadOnly(stagingDirectory);
            Directory.Move(stagingDirectory, finalDirectory);
            moved = true;
            Activate(root, verified.Version);
            audit.Add(
                context,
                AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
                "website.release.publish",
                "WebsiteRelease",
                verified.Version,
                AuditResult.Succeeded,
                reason.Trim(),
                requestContext,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return new WebsiteReleaseV1(verified.Version, now, verified.ArchiveSha256, verified.ArchiveSizeBytes, true, false);
        }
        catch
        {
            RestoreLinks(root, previousActive, previousRollback);
            if (moved) DeleteDirectory(finalDirectory); else DeleteDirectory(stagingDirectory);
            throw;
        }
        finally
        {
            DeleteFile(stagedArchive);
        }
    }

    public async Task<WebsiteReleaseV1> ActivateAsync(
        string version,
        WebsiteReleaseActivationRequestV1 request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(request.Reason);
        if (!TryVersion(version)) throw new ArgumentException("The website version is invalid.", nameof(version));
        var root = EnsureStorageRoot();
        var releaseDirectory = Path.Combine(root, "releases", version);
        var metadataPath = Path.Combine(releaseDirectory, ".peeronq-release.json");
        if (!Directory.Exists(releaseDirectory) || !File.Exists(metadataPath)) throw new KeyNotFoundException();
        var metadata = JsonSerializer.Deserialize<WebsiteReleaseMetadata>(await File.ReadAllBytesAsync(metadataPath, cancellationToken), JsonOptions)
            ?? throw new InvalidOperationException("Website release metadata is invalid.");
        if (metadata.Version != version) throw new InvalidOperationException("Website release metadata does not match its directory.");

        var previousActive = ReadLinkVersion(root, "current");
        var previousRollback = ReadLinkVersion(root, "previous");
        if (previousActive == version)
            throw new ApiProblemException(StatusCodes.Status409Conflict, "website_release_active", "This website release is already active.");
        try
        {
            Activate(root, version);
            var now = timeProvider.GetUtcNow();
            audit.Add(
                context,
                AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
                "website.release.activate",
                "WebsiteRelease",
                version,
                AuditResult.Succeeded,
                request.Reason.Trim(),
                requestContext,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return new WebsiteReleaseV1(version, metadata.PublishedAtUtc, metadata.ArchiveSha256, metadata.ArchiveSizeBytes, true, false);
        }
        catch
        {
            RestoreLinks(root, previousActive, previousRollback);
            throw;
        }
    }

    private string EnsureStorageRoot()
    {
        if (!_options.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "website_publication_disabled", "Website publication is not configured.");
        var root = Path.GetFullPath(_options.StorageDirectory);
        EnsureDirectory(root);
        EnsureDirectory(Path.Combine(root, ".staging"));
        EnsureDirectory(Path.Combine(root, "releases"));
        return root;
    }

    private async Task CopyAndVerifyArchiveAsync(IFormFile archive, string destination, VerifiedWebsiteManifest verified, CancellationToken cancellationToken)
    {
        await using var source = archive.OpenReadStream();
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > verified.ArchiveSizeBytes || total > _options.MaximumArchiveBytes)
                throw Invalid("website_archive_mismatch", "The uploaded website archive exceeds the signed size.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
        if (total != verified.ArchiveSizeBytes
            || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(verified.ArchiveSha256)))
            throw Invalid("website_archive_integrity_mismatch", "The uploaded website archive does not match the signed SHA-256 digest.");
    }

    internal static void ExtractVerifiedArchive(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > WebsitePublicationOptions.MaximumEntries)
            throw Invalid("website_archive_invalid", "The website archive contains an invalid number of entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        ZipArchiveEntry? indexEntry = null;
        ZipArchiveEntry? downloadsUiEntry = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = ValidateEntry(entry, names);
            if (entry.Name.Length == 0) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > WebsitePublicationOptions.MaximumExpandedBytes)
                throw Invalid("website_archive_expanded_limit", "The expanded website archive is too large.");
            if (normalized == "index.html") indexEntry = entry;
            if (normalized == DownloadsUiCompatibilityEntry) downloadsUiEntry = entry;
        }
        if (indexEntry is null) throw Invalid("website_entry_point_missing", "The website archive does not contain index.html.");
        if (downloadsUiEntry is null
            || indexEntry.Length != downloadsUiEntry.Length
            || !EntriesHaveEqualSha256(indexEntry, downloadsUiEntry))
            throw Invalid("website_downloads_ui_incompatible", "The website archive does not contain a verified Downloads UI compatibility entry.");

        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, normalized));
            if (!target.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw Invalid("website_archive_path_invalid", "A website archive path escaped the release directory.");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
    }

    private static bool EntriesHaveEqualSha256(ZipArchiveEntry left, ZipArchiveEntry right)
    {
        using var leftStream = left.Open();
        using var rightStream = right.Open();
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leftStream), SHA256.HashData(rightStream));
    }

    private static string ValidateEntry(ZipArchiveEntry entry, HashSet<string> names)
    {
        var name = entry.FullName;
        if (name.Length is 0 or > 240 || name.StartsWith('/') || name.StartsWith('\\') || name.Contains('\\')
            || name.Contains(':') || name.Contains('\0') || name.Contains("//", StringComparison.Ordinal))
            throw Invalid("website_archive_path_invalid", "The website archive contains an unsafe path.");
        var normalized = name.TrimEnd('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".." || !SafeSegment().IsMatch(segment)))
            throw Invalid("website_archive_path_invalid", "The website archive contains an unsafe path segment.");
        if (!names.Add(normalized))
            throw Invalid("website_archive_duplicate", "The website archive contains duplicate paths.");

        var unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
        var fileType = unixMode & 0xf000;
        var isDirectory = entry.Name.Length == 0;
        if ((fileType != 0 && fileType != 0x4000 && fileType != 0x8000)
            || (fileType == 0x4000 && !isDirectory)
            || (fileType == 0x8000 && isDirectory)
            || (!isDirectory && (unixMode & 0x49) != 0)
            || (isDirectory && entry.Length != 0))
            throw Invalid("website_archive_file_type_invalid", "The website archive contains a link, device, or executable file.");
        if (!isDirectory && !AllowedExtensions.Contains(Path.GetExtension(normalized)))
            throw Invalid("website_archive_extension_invalid", "The website archive contains a file type that cannot be published.");
        return normalized;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();

    private static async Task WriteMetadataAsync(string directory, VerifiedWebsiteManifest verified, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        var metadata = new WebsiteReleaseMetadata(verified.Version, publishedAt, verified.ArchiveSha256, verified.ArchiveSizeBytes, verified.ManifestDigest);
        await File.WriteAllBytesAsync(Path.Combine(directory, ".peeronq-release.json"), JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions), cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, ".peeronq-manifest.json"), verified.EnvelopeBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadManifestAsync(IFormFile manifest, CancellationToken cancellationToken)
    {
        if (manifest.Length is <= 0 or > WebsitePublicationOptions.MaximumManifestBytes)
            throw Invalid("website_manifest_invalid", "The signed website manifest has an invalid size.");
        await using var stream = manifest.OpenReadStream();
        using var buffer = new MemoryStream((int)manifest.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != manifest.Length) throw Invalid("website_manifest_invalid", "The signed website manifest changed during upload.");
        return buffer.ToArray();
    }

    private static void Activate(string root, string version)
    {
        var current = ReadLinkVersion(root, "current");
        if (current is not null) AtomicLink(root, "previous", current);
        AtomicLink(root, "current", version);
    }

    private static void RestoreLinks(string root, string? current, string? previous)
    {
        RestoreLink(root, "current", current);
        RestoreLink(root, "previous", previous);
    }

    private static void RestoreLink(string root, string name, string? version)
    {
        if (version is null) DeleteFile(Path.Combine(root, name));
        else AtomicLink(root, name, version);
    }

    private static void AtomicLink(string root, string name, string version)
    {
        if (!TryVersion(version) || !Directory.Exists(Path.Combine(root, "releases", version)))
            throw new InvalidOperationException("Website release link target is unsafe or missing.");
        var link = Path.Combine(root, name);
        var temporary = Path.Combine(root, $".{name}.{Guid.NewGuid():N}.new");
        try
        {
            File.CreateSymbolicLink(temporary, Path.Combine("releases", version));
            File.Move(temporary, link, overwrite: true);
        }
        finally
        {
            DeleteFile(temporary);
        }
    }

    private static string? ReadLinkVersion(string root, string name)
    {
        var link = new FileInfo(Path.Combine(root, name));
        var target = link.LinkTarget;
        if (target is null) return null;
        var expectedPrefix = $"releases{Path.DirectorySeparatorChar}";
        var normalized = target.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (!normalized.StartsWith(expectedPrefix, StringComparison.Ordinal) || normalized[expectedPrefix.Length..].Contains(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Website release link target is outside controlled storage.");
        var version = normalized[expectedPrefix.Length..];
        if (!TryVersion(version)) throw new InvalidOperationException("Website release link target is invalid.");
        return version;
    }

    private static bool TryVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && Version.TryParse(value, out _)
        && value.All(character => char.IsAsciiDigit(character) || character == '.');

    private static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Website publication storage cannot be a reparse point.");
    }

    private static void MakeTreePublicReadOnly(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 3 or > 512)
            throw new ArgumentException("A bounded audit reason is required.", nameof(reason));
    }

    private static ApiProblemException Invalid(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);

    private sealed record WebsiteReleaseMetadata(
        string Version,
        DateTimeOffset PublishedAtUtc,
        string ArchiveSha256,
        long ArchiveSizeBytes,
        string ManifestDigest);
}

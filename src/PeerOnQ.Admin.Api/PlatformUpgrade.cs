using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api;

public sealed class PlatformUpgradeOptions
{
    public const string SectionName = "PeerOnQ:PlatformUpgrade";
    public const long MaximumBundleBytesLimit = 256L * 1024 * 1024;
    public const long MaximumRequestBodyBytes = 260L * 1024 * 1024;
    public const int MaximumChecksumBytes = 4 * 1024;
    public const int MaximumSignatureBytes = 128 * 1024;
    public const int MaximumStatusBytes = 16 * 1024;

    public bool Enabled { get; set; }
    public string RequestSpoolDirectory { get; set; } = "/var/lib/peeronq/platform-upgrade/inbox";
    public string StatusDirectory { get; set; } = "/var/lib/peeronq/platform-upgrade/status";
    public long MaximumBundleBytes { get; set; } = MaximumBundleBytesLimit;
}

internal sealed class PlatformUpgradeOptionsValidator : IValidateOptions<PlatformUpgradeOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformUpgradeOptions options)
    {
        if (options.MaximumBundleBytes is <= 0 or > PlatformUpgradeOptions.MaximumBundleBytesLimit)
            return ValidateOptionsResult.Fail("The platform-upgrade bundle limit is invalid.");
        if (!options.Enabled) return ValidateOptionsResult.Success;
        if (!OperatingSystem.IsLinux())
            return ValidateOptionsResult.Fail("Platform upgrades can only be enabled on a Linux host.");

        if (!TryNormalizeRoot(options.RequestSpoolDirectory, out var requestRoot)
            || !TryNormalizeRoot(options.StatusDirectory, out var statusRoot))
            return ValidateOptionsResult.Fail("Platform-upgrade spool and status directories must be absolute non-root paths.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (requestRoot.Equals(statusRoot, comparison)
            || IsWithin(requestRoot, statusRoot, comparison)
            || IsWithin(statusRoot, requestRoot, comparison))
            return ValidateOptionsResult.Fail("Platform-upgrade request and status directories must be disjoint.");

        return ValidateOptionsResult.Success;
    }

    private static bool TryNormalizeRoot(string value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            normalized = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.Length > 0 && !string.Equals(normalized, root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsWithin(string path, string parent, StringComparison comparison) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
}

public sealed record PlatformUpgradeCheckV1(string Code, string Label, string State, string Message);

public sealed record PlatformUpgradeStatusV1(
    bool Enabled,
    string Environment,
    int SchemaVersion,
    string State,
    string? OperationId,
    string? CurrentVersion,
    string? TargetVersion,
    string? RollbackVersion,
    int ProgressPercent,
    bool CanApply,
    bool CanRollback,
    string? BlockingReason,
    string Message,
    string? LogReference,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PlatformUpgradeCheckV1> Checks);

public sealed record PlatformUpgradeActionRequestV1(
    string TargetVersion,
    string ExpectedCurrentVersion,
    string Reason);

public sealed record PlatformUpgradeAcceptedV1(
    string RequestId,
    string Action,
    string TargetVersion,
    string State,
    DateTimeOffset AcceptedAtUtc);

internal sealed record PlatformUpgradeStatusSnapshot(PlatformUpgradeStatusV1 Response, bool IsTrusted);

internal interface IPlatformUpgradeStatusReader
{
    PlatformUpgradeStatusSnapshot Read();
}

internal sealed class PlatformUpgradeStatusReader(
    IOptions<PlatformUpgradeOptions> configuredOptions,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<PlatformUpgradeStatusReader> logger) : IPlatformUpgradeStatusReader
{
    private const string StatusFileName = "status.json";
    private readonly PlatformUpgradeOptions _options = configuredOptions.Value;

    public PlatformUpgradeStatusSnapshot Read()
    {
        if (!_options.Enabled)
            return Unavailable(false, "platform_upgrade_disabled", "Platform upgrades are not configured.");

        try
        {
            var root = Path.GetFullPath(_options.StatusDirectory);
            var path = Path.Combine(root, StatusFileName);
            PlatformUpgradeFileTrust.EnsureTrustedStatusDirectory(root);
            PlatformUpgradeFileTrust.EnsureTrustedStatusFile(path);
            var bytes = ReadBoundedFile(path, PlatformUpgradeOptions.MaximumStatusBytes);
            var document = PlatformUpgradeStatusValidation.Parse(bytes);
            return new PlatformUpgradeStatusSnapshot(new PlatformUpgradeStatusV1(
                true,
                environment.EnvironmentName,
                document.SchemaVersion,
                document.State,
                document.OperationId,
                document.CurrentVersion,
                document.TargetVersion,
                document.RollbackVersion,
                document.ProgressPercent,
                document.CanApply,
                document.CanRollback,
                document.BlockingReason,
                document.Message,
                document.LogReference,
                document.UpdatedAtUtc,
                document.Checks.Select(value => new PlatformUpgradeCheckV1(
                    value.Code, value.Label, value.State, value.Message)).ToArray()), true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or ArgumentException)
        {
            logger.LogWarning(
                "Platform upgrade status is unavailable with {EventName} and {ExceptionType}",
                "platform.upgrade.status.unavailable",
                exception.GetType().Name);
            return Unavailable(true, "platform_upgrade_status_unavailable", "Platform upgrade status is unavailable.");
        }
    }

    private PlatformUpgradeStatusSnapshot Unavailable(bool enabled, string reason, string message) => new(
        new PlatformUpgradeStatusV1(
            enabled,
            environment.EnvironmentName,
            1,
            "idle",
            null,
            null,
            null,
            null,
            0,
            false,
            false,
            reason,
            message,
            null,
            timeProvider.GetUtcNow(),
            []),
        false);

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The platform-upgrade status file has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

internal static class PlatformUpgradeFileTrust
{
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtNoAutomount = 0x800;
    private const uint StatxRequiredMask = 0x1f;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryType = 0x4000;
    private const uint GroupOrOtherWrite = 0x12;
    private const uint PermissionsMask = 0x1ff;
    private const uint AdminMarkerPermissions = 0x1a0;

    public static void EnsureTrustedStatusDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            EnsureLinuxTrust(path, DirectoryType, requireSingleLink: false);
            return;
        }
        if (!Directory.Exists(path) || HasReparsePoint(path))
            throw new InvalidDataException("The platform-upgrade status directory is unavailable.");
    }

    public static void EnsureTrustedStatusFile(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            EnsureLinuxTrust(path, RegularFile, requireSingleLink: true);
            return;
        }
        if (!File.Exists(path) || HasReparsePoint(path))
            throw new InvalidDataException("The platform-upgrade status file is unavailable.");
    }

    public static bool IsRegularFileNoFollow(string path)
    {
        if (OperatingSystem.IsLinux())
            return TryGetLinuxStatus(path, out var status) && (status.Mode & FileTypeMask) == RegularFile;
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) == 0;
    }

    public static bool IsAdminActiveMarkerNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux()) return IsRegularFileNoFollow(path);
        return TryGetLinuxStatus(path, out var status)
            && (status.Mode & FileTypeMask) == RegularFile
            && (status.Mode & PermissionsMask) == AdminMarkerPermissions;
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void EnsureLinuxTrust(string path, uint expectedType, bool requireSingleLink)
    {
        if (!TryGetLinuxStatus(path, out var status)
            || (status.Mode & FileTypeMask) != expectedType
            || status.UserId != 0
            || (status.Mode & GroupOrOtherWrite) != 0
            || (requireSingleLink && status.LinkCount != 1))
            throw new InvalidDataException("The platform-upgrade status ownership or mode is invalid.");
    }

    private static bool TryGetLinuxStatus(string path, out LinuxStatx status)
    {
        try
        {
            return Statx(
                       AtCurrentWorkingDirectory,
                       path,
                       AtSymlinkNoFollow | AtNoAutomount,
                       StatxRequiredMask,
                       out status) == 0
                   && (status.Mask & StatxRequiredMask) == StatxRequiredMask;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidDataException("Linux statx is required for platform-upgrade file trust.", exception);
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxStatx status);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(24)] public uint GroupId;
        [FieldOffset(28)] public ushort Mode;
    }
}

internal sealed record PlatformUpgradeStatusDocument
{
    public required int SchemaVersion { get; init; }
    public required string State { get; init; }
    public required string? OperationId { get; init; }
    public required string? CurrentVersion { get; init; }
    public required string? TargetVersion { get; init; }
    public required string? RollbackVersion { get; init; }
    public required int ProgressPercent { get; init; }
    public required bool CanApply { get; init; }
    public required bool CanRollback { get; init; }
    public required string? BlockingReason { get; init; }
    public required string Message { get; init; }
    public required string? LogReference { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required IReadOnlyList<PlatformUpgradeStatusCheckDocument> Checks { get; init; }
}

internal sealed record PlatformUpgradeStatusCheckDocument
{
    public required string Code { get; init; }
    public required string Label { get; init; }
    public required string State { get; init; }
    public required string Message { get; init; }
}

internal static partial class PlatformUpgradeStatusValidation
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "idle", "queued", "verifying", "preflight", "ready", "applying",
        "verifying_deployment", "succeeded", "failed", "rolling_back", "rolled_back",
    };

    private static readonly HashSet<string> CheckStates = new(StringComparer.Ordinal)
    {
        "pending", "passed", "warning", "failed",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCodePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LogReferencePattern();

    public static PlatformUpgradeStatusDocument Parse(ReadOnlySpan<byte> bytes)
    {
        PlatformUpgradeStatusDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PlatformUpgradeStatusDocument>(bytes, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The platform-upgrade status document is malformed.");
        }

        if (document.SchemaVersion != 1 || document.State is null || !States.Contains(document.State)) Invalid();
        if (document.OperationId is not null && !OperationIdPattern().IsMatch(document.OperationId)) Invalid();
        if (document.State == "idle" ? document.OperationId is not null : document.OperationId is null) Invalid();
        ValidateVersion(document.CurrentVersion);
        ValidateVersion(document.TargetVersion);
        ValidateVersion(document.RollbackVersion);
        if (document.ProgressPercent is < 0 or > 100) Invalid();
        if (document.CanApply
            && (document.State != "ready"
                || document.CurrentVersion is null
                || document.TargetVersion is null)) Invalid();
        if (document.CanRollback
            && (document.State != "succeeded"
                || document.CurrentVersion is null
                || document.RollbackVersion is null)) Invalid();
        if (document.BlockingReason is not null && !SafeCodePattern().IsMatch(document.BlockingReason)) Invalid();
        ValidateText(document.Message, 512, required: true);
        if (document.LogReference is not null && !LogReferencePattern().IsMatch(document.LogReference)) Invalid();
        if (document.UpdatedAtUtc.Offset != TimeSpan.Zero || document.Checks is null || document.Checks.Count > 32) Invalid();
        foreach (var check in document.Checks)
        {
            if (check is null
                || check.Code is null
                || check.State is null
                || !SafeCodePattern().IsMatch(check.Code)
                || !CheckStates.Contains(check.State)) Invalid();
            ValidateText(check.Label, 128, required: true);
            ValidateText(check.Message, 512, required: true);
        }

        return document;
    }

    private static void ValidateVersion(string? value)
    {
        if (value is not null && !PlatformSemanticVersion.TryParse(value, out _)) Invalid();
    }

    private static void ValidateText(string? value, int maximumLength, bool required)
    {
        if ((required && string.IsNullOrWhiteSpace(value))
            || value is { Length: > 0 } && (value.Length > maximumLength || value.Any(char.IsControl)))
            Invalid();
    }

    [DoesNotReturn]
    private static void Invalid() =>
        throw new InvalidDataException("The platform-upgrade status document is invalid.");
}

internal sealed record PlatformUpgradeUploadDescriptor(
    string Version,
    string BundleFileName,
    string ChecksumFileName,
    string SignatureFileName,
    string ExpectedSha256,
    byte[] ChecksumBytes,
    byte[] SignatureBytes);

internal sealed partial class PlatformUpgradeRequestSpool(IOptions<PlatformUpgradeOptions> configuredOptions)
{
    internal const string RequestFileName = "request.env";
    private const string ActiveRequestFileName = ".active-request";
    private readonly PlatformUpgradeOptions _options = configuredOptions.Value;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex("^peeronq-server-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)\\.run$", RegexOptions.CultureInvariant)]
    private static partial Regex BundleNamePattern();

    public async Task<PlatformUpgradeUploadDescriptor> ValidateUploadAsync(
        IFormFile bundle,
        IFormFile checksum,
        IFormFile signature,
        CancellationToken cancellationToken)
    {
        if (bundle.Length is <= 0 || bundle.Length > _options.MaximumBundleBytes)
            throw Invalid("platform_upgrade_bundle_size_invalid", "The platform-upgrade bundle size is invalid.");
        var bundleName = ValidateBareFileName(bundle.FileName);
        var match = BundleNamePattern().Match(bundleName);
        if (!match.Success || !PlatformSemanticVersion.TryParse(match.Groups["version"].Value, out var parsed))
            throw Invalid("platform_upgrade_bundle_name_invalid", "The platform-upgrade bundle filename is invalid.");

        var checksumName = ValidateBareFileName(checksum.FileName);
        var signatureName = ValidateBareFileName(signature.FileName);
        if (checksumName != bundleName + ".sha256" || signatureName != bundleName + ".asc")
            throw Invalid("platform_upgrade_companion_name_invalid", "The checksum or signature filename does not match the bundle.");

        var checksumBytes = await ReadBoundedAsync(
            checksum, PlatformUpgradeOptions.MaximumChecksumBytes, "platform_upgrade_checksum_invalid", cancellationToken);
        var signatureBytes = await ReadBoundedAsync(
            signature, PlatformUpgradeOptions.MaximumSignatureBytes, "platform_upgrade_signature_invalid", cancellationToken);
        var expectedHash = ParseChecksum(checksumBytes, bundleName);
        ValidateArmoredSignature(signatureBytes);
        return new PlatformUpgradeUploadDescriptor(
            parsed.Original,
            bundleName,
            checksumName,
            signatureName,
            expectedHash,
            checksumBytes,
            signatureBytes);
    }

    public async Task<PreparedPlatformUpgradeRequest> PrepareStageAsync(
        PlatformUpgradeUploadDescriptor descriptor,
        IFormFile bundle,
        string expectedCurrentVersion,
        CancellationToken cancellationToken)
    {
        var prepared = Begin("stage");
        try
        {
            var bundlePath = Path.Combine(prepared.RequestDirectory, descriptor.BundleFileName);
            prepared.Track(bundlePath);
            var (size, hash) = await CopyBundleAsync(bundle, bundlePath, cancellationToken);
            if (size != bundle.Length
                || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(descriptor.ExpectedSha256)))
                throw Invalid("platform_upgrade_bundle_integrity_mismatch", "The bundle does not match its checksum.");

            var checksumPath = Path.Combine(prepared.RequestDirectory, descriptor.ChecksumFileName);
            var signaturePath = Path.Combine(prepared.RequestDirectory, descriptor.SignatureFileName);
            await prepared.WriteAsync(checksumPath, descriptor.ChecksumBytes, cancellationToken);
            await prepared.WriteAsync(signaturePath, descriptor.SignatureBytes, cancellationToken);
            await prepared.WriteRequestAsync([
                "schema=1",
                $"request_id={prepared.RequestId}",
                "action=stage",
                $"version={descriptor.Version}",
                $"expected_current={expectedCurrentVersion}",
                $"bundle_file={descriptor.BundleFileName}",
                $"checksum_file={descriptor.ChecksumFileName}",
                $"signature_file={descriptor.SignatureFileName}",
                $"bundle_sha256={descriptor.ExpectedSha256}",
                $"bundle_size={size.ToString(CultureInfo.InvariantCulture)}",
            ], cancellationToken);
            return prepared;
        }
        catch
        {
            await prepared.DisposeAsync();
            throw;
        }
    }

    public async Task<PreparedPlatformUpgradeRequest> PrepareActionAsync(
        string action,
        string version,
        string expectedCurrentVersion,
        CancellationToken cancellationToken)
    {
        if (action is not ("apply" or "rollback")) throw new ArgumentOutOfRangeException(nameof(action));
        var prepared = Begin(action);
        try
        {
            await prepared.WriteRequestAsync([
                "schema=1",
                $"request_id={prepared.RequestId}",
                $"action={action}",
                $"version={version}",
                $"expected_current={expectedCurrentVersion}",
            ], cancellationToken);
            return prepared;
        }
        catch
        {
            await prepared.DisposeAsync();
            throw;
        }
    }

    private PreparedPlatformUpgradeRequest Begin(string action)
    {
        EnsureAvailable();
        var requestId = Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(_options.RequestSpoolDirectory);
        var activePath = Path.Combine(root, ActiveRequestFileName);
        var ownsActive = false;
        try
        {
            using var active = CreateActiveFile(activePath);
            ownsActive = true;
            var activeBytes = Encoding.ASCII.GetBytes(requestId + "\n");
            active.Write(activeBytes);
            active.Flush(flushToDisk: true);
            SetMode(activePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        catch (IOException) when (!ownsActive && File.Exists(activePath))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "platform_upgrade_busy", "Another platform-upgrade request is active.");
        }
        catch
        {
            if (ownsActive) DeleteOwnedFile(activePath);
            throw;
        }

        var requestDirectory = Path.Combine(root, requestId);
        try
        {
            if (Directory.Exists(requestDirectory) || File.Exists(requestDirectory))
                throw new IOException("The platform-upgrade request identifier already exists.");
            Directory.CreateDirectory(requestDirectory);
            if ((File.GetAttributes(requestDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The platform-upgrade request directory is unsafe.");
            SetMode(requestDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            return new PreparedPlatformUpgradeRequest(requestId, action, requestDirectory, activePath);
        }
        catch
        {
            DeleteActive(activePath, requestId);
            throw;
        }
    }

    private static FileStream CreateActiveFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        return new FileStream(path, options);
    }

    private void EnsureAvailable()
    {
        if (!_options.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "platform_upgrade_disabled", "Platform upgrades are not configured.");
        var root = Path.GetFullPath(_options.RequestSpoolDirectory);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "platform_upgrade_spool_unavailable", "The platform-upgrade request spool is unavailable.");
    }

    private async Task<(long Size, byte[] Hash)> CopyBundleAsync(
        IFormFile sourceFile,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var source = sourceFile.OpenReadStream();
        await using var target = CreateNewFile(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > _options.MaximumBundleBytes || total > sourceFile.Length)
                throw Invalid("platform_upgrade_bundle_size_invalid", "The platform-upgrade bundle exceeds its declared size.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
        SetMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        return (total, hash.GetHashAndReset());
    }

    private static FileStream CreateNewFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = 128 * 1024,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
    });

    private static async Task<byte[]> ReadBoundedAsync(
        IFormFile file,
        int maximumBytes,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 || file.Length > maximumBytes)
            throw Invalid(errorCode, "The uploaded companion file has an invalid size.");
        await using var source = file.OpenReadStream();
        using var destination = new MemoryStream((int)file.Length);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > file.Length || destination.Length + read > maximumBytes)
                throw Invalid(errorCode, "The uploaded companion file exceeds its declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (destination.Length != file.Length)
            throw Invalid(errorCode, "The uploaded companion file changed while it was read.");
        return destination.ToArray();
    }

    private static string ParseChecksum(byte[] bytes, string bundleName)
    {
        string value;
        try { value = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw Invalid("platform_upgrade_checksum_invalid", "The platform-upgrade checksum is not valid UTF-8.");
        }
        var expectedSuffix = "  " + bundleName;
        var line = value.TrimEnd('\r', '\n');
        if (value != line && value != line + "\n" && value != line + "\r\n"
            || line.Length != 64 + expectedSuffix.Length
            || !line.EndsWith(expectedSuffix, StringComparison.Ordinal))
            throw Invalid("platform_upgrade_checksum_invalid", "The platform-upgrade checksum file is malformed.");
        var hash = line[..64];
        if (hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("platform_upgrade_checksum_invalid", "The platform-upgrade checksum is invalid.");
        return hash;
    }

    private static void ValidateArmoredSignature(byte[] bytes)
    {
        if (bytes.Any(value => value is 0 or > 0x7f))
            throw Invalid("platform_upgrade_signature_invalid", "The detached signature is malformed.");
        var text = Encoding.ASCII.GetString(bytes).TrimEnd('\r', '\n');
        if ((!text.StartsWith("-----BEGIN PGP SIGNATURE-----\n", StringComparison.Ordinal)
                && !text.StartsWith("-----BEGIN PGP SIGNATURE-----\r\n", StringComparison.Ordinal))
            || !text.EndsWith("-----END PGP SIGNATURE-----", StringComparison.Ordinal))
            throw Invalid("platform_upgrade_signature_invalid", "The detached signature is not an armored PGP signature.");
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw Invalid("platform_upgrade_signature_invalid", "The detached signature contains invalid characters.");
    }

    private static string ValidateBareFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Contains('/')
            || value.Contains('\\')
            || Path.GetFileName(value) != value)
            throw Invalid("platform_upgrade_filename_invalid", "The uploaded filename is invalid.");
        return value;
    }

    internal static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    internal static void DeleteActive(string path, string requestId)
    {
        try
        {
            if (!PlatformUpgradeFileTrust.IsAdminActiveMarkerNoFollow(path) || new FileInfo(path).Length > 64) return;
            if (File.ReadAllText(path, Encoding.ASCII).TrimEnd('\r', '\n') == requestId) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static void DeleteOwnedFile(string path)
    {
        try
        {
            if (PlatformUpgradeFileTrust.IsRegularFileNoFollow(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static ApiProblemException Invalid(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);
}

internal sealed class PreparedPlatformUpgradeRequest(
    string requestId,
    string action,
    string requestDirectory,
    string activePath) : IAsyncDisposable
{
    private readonly List<string> _ownedFiles = [];
    private bool _committed;

    public string RequestId { get; } = requestId;
    public string Action { get; } = action;
    public string RequestDirectory { get; } = requestDirectory;

    public void Track(string path) => _ownedFiles.Add(path);

    public async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Track(path);
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 16 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        });
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        PlatformUpgradeRequestSpool.SetMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    public async Task WriteRequestAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var path = Path.Combine(RequestDirectory, PlatformUpgradeRequestSpool.RequestFileName);
        var bytes = new UTF8Encoding(false).GetBytes(string.Join('\n', lines) + "\n");
        await WriteAsync(path, bytes, cancellationToken);
    }

    public void Commit()
    {
        var root = Directory.GetParent(RequestDirectory)?.FullName
            ?? throw new InvalidOperationException("The platform-upgrade request root is unavailable.");
        var readyPath = Path.Combine(root, RequestId + ".ready");
        var temporaryPath = Path.Combine(RequestDirectory, ".ready.tmp");
        Track(temporaryPath);
        using (var marker = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                   FileOptions.WriteThrough))
        {
            marker.Flush(flushToDisk: true);
        }
        PlatformUpgradeRequestSpool.SetMode(temporaryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(temporaryPath, readyPath);
        _committed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_committed) return ValueTask.CompletedTask;
        foreach (var path in _ownedFiles.AsEnumerable().Reverse())
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        try { if (Directory.Exists(RequestDirectory)) Directory.Delete(RequestDirectory, recursive: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        PlatformUpgradeRequestSpool.DeleteActive(activePath, RequestId);
        return ValueTask.CompletedTask;
    }
}

internal sealed class PlatformUpgradeUploadGate
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

internal sealed class PlatformUpgradeService(
    PlatformUpgradeRequestSpool spool,
    IPlatformUpgradeStatusReader statusReader,
    ICloudUnitOfWork unitOfWork,
    AdminAuditWriter audit,
    AdminRequestContext requestContext,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> RunningStates = new(StringComparer.Ordinal)
    {
        "queued", "verifying", "preflight", "applying", "verifying_deployment", "rolling_back",
    };

    public PlatformUpgradeStatusV1 GetStatus() => statusReader.Read().Response;

    public async Task<PlatformUpgradeAcceptedV1> StageAsync(
        IFormFile bundle,
        IFormFile checksum,
        IFormFile signature,
        string reason,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var descriptor = await spool.ValidateUploadAsync(bundle, checksum, signature, cancellationToken);
        var status = RequireTrustedStatus();
        var current = RequireVersion(status.CurrentVersion, "platform_upgrade_current_version_unknown");
        var target = RequireVersion(descriptor.Version, "platform_upgrade_version_invalid");
        if (RunningStates.Contains(status.State) || status.State == "ready")
            throw Conflict("platform_upgrade_busy", "A platform upgrade is already staged or running.");
        if (status.TargetVersion == descriptor.Version)
            throw Conflict("platform_upgrade_replay", "This platform-upgrade version has already been processed.");
        if (target.CompareTo(current) <= 0)
            throw Conflict("platform_upgrade_version_not_newer", "The staged platform version must be newer than the active version.");

        await using var prepared = await spool.PrepareStageAsync(
            descriptor, bundle, current.Original, cancellationToken);
        await AuditAndAuthorizeAsync(
            prepared,
            "platform.upgrade.stage.authorize",
            descriptor.Version,
            reason,
            context,
            cancellationToken);
        return Accepted(prepared, descriptor.Version);
    }

    public Task<PlatformUpgradeAcceptedV1> ApplyAsync(
        PlatformUpgradeActionRequestV1 request,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AuthorizeActionAsync("apply", request, context, cancellationToken);

    public Task<PlatformUpgradeAcceptedV1> RollbackAsync(
        PlatformUpgradeActionRequestV1 request,
        HttpContext context,
        CancellationToken cancellationToken) =>
        AuthorizeActionAsync("rollback", request, context, cancellationToken);

    private async Task<PlatformUpgradeAcceptedV1> AuthorizeActionAsync(
        string action,
        PlatformUpgradeActionRequestV1 request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ValidateReason(request.Reason);
        var requestedTarget = RequireVersion(request.TargetVersion, "platform_upgrade_version_invalid");
        var requestedCurrent = RequireVersion(request.ExpectedCurrentVersion, "platform_upgrade_current_version_invalid");
        var status = RequireTrustedStatus();
        var active = RequireVersion(status.CurrentVersion, "platform_upgrade_current_version_unknown");
        if (RunningStates.Contains(status.State))
            throw Conflict("platform_upgrade_busy", "A platform upgrade is already running.");
        if (requestedCurrent.Original != active.Original)
            throw Conflict("platform_upgrade_current_version_mismatch", "The active platform version changed. Refresh status before continuing.");

        if (action == "apply")
        {
            if (status.State != "ready" || !status.CanApply || status.TargetVersion != requestedTarget.Original)
                throw Conflict("platform_upgrade_not_ready", "The requested platform version is not ready to apply.");
            if (requestedTarget.CompareTo(active) <= 0)
                throw Conflict("platform_upgrade_version_not_newer", "The applied platform version must be newer than the active version.");
        }
        else
        {
            if (status.State != "succeeded"
                || !status.CanRollback
                || status.RollbackVersion != requestedTarget.Original)
                throw Conflict("platform_upgrade_rollback_unavailable", "The requested rollback version is not available.");
            if (requestedTarget.CompareTo(active) >= 0)
                throw Conflict("platform_upgrade_rollback_invalid", "The rollback version must precede the active version.");
        }

        await using var prepared = await spool.PrepareActionAsync(
            action, requestedTarget.Original, active.Original, cancellationToken);
        await AuditAndAuthorizeAsync(
            prepared,
            $"platform.upgrade.{action}.authorize",
            requestedTarget.Original,
            request.Reason,
            context,
            cancellationToken);
        return Accepted(prepared, requestedTarget.Original);
    }

    private async Task AuditAndAuthorizeAsync(
        PreparedPlatformUpgradeRequest prepared,
        string auditAction,
        string targetVersion,
        string reason,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        audit.Add(
            context,
            AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
            auditAction,
            "PlatformRelease",
            $"{targetVersion}@{prepared.RequestId}",
            AuditResult.Succeeded,
            reason.Trim(),
            requestContext,
            timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        prepared.Commit();
    }

    private PlatformUpgradeStatusV1 RequireTrustedStatus()
    {
        var snapshot = statusReader.Read();
        if (!snapshot.Response.Enabled)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "platform_upgrade_disabled", "Platform upgrades are not configured.");
        if (!snapshot.IsTrusted)
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "platform_upgrade_status_unavailable", "Platform upgrade status is unavailable.");
        return snapshot.Response;
    }

    private PlatformUpgradeAcceptedV1 Accepted(PreparedPlatformUpgradeRequest prepared, string targetVersion) => new(
        prepared.RequestId,
        prepared.Action,
        targetVersion,
        "queued",
        timeProvider.GetUtcNow());

    private static PlatformSemanticVersion RequireVersion(string? value, string code)
    {
        if (value is null || !PlatformSemanticVersion.TryParse(value, out var parsed))
            throw Conflict(code, "The platform version is invalid or unavailable.");
        return parsed;
    }

    private static void ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length is < 3 or > 512
            || reason.Any(char.IsControl))
            throw new ArgumentException("A bounded audit reason is required.", nameof(reason));
    }

    private static ApiProblemException Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, code, detail);
}

internal sealed partial class PlatformSemanticVersion : IComparable<PlatformSemanticVersion>
{
    private PlatformSemanticVersion(string original, int major, int minor, int patch)
    {
        Original = original;
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public string Original { get; }
    private int Major { get; }
    private int Minor { get; }
    private int Patch { get; }

    public static bool TryParse(string value, [NotNullWhen(true)] out PlatformSemanticVersion? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 29) return false;
        var match = SemanticVersionPattern().Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;
        parsed = new PlatformSemanticVersion(value, major, minor, patch);
        return true;
    }

    public int CompareTo(PlatformSemanticVersion? other)
    {
        if (other is null) return 1;
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        return core;
    }

    [GeneratedRegex("^(?<major>0|[1-9][0-9]{0,8})\\.(?<minor>0|[1-9][0-9]{0,8})\\.(?<patch>0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}

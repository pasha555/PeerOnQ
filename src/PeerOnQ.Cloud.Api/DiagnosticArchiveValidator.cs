using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PeerOnQ.Cloud.Application;

namespace PeerOnQ.Cloud.Api;

internal static partial class DiagnosticArchiveValidator
{
    private const int MaximumEntries = 6;
    private const int MaximumEntryNameBytes = 64;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumLogBytes = 2 * 1024 * 1024;
    private const long MaximumExpandedBytes = MaximumManifestBytes + (5L * MaximumLogBytes);
    private const int MaximumCompressionRatio = 250;
    private const int MaximumCentralDirectoryBytes = 32 * 1024;
    private const int MaximumExtraFieldBytes = 1024;
    private const ushort AllowedGeneralPurposeFlags = 0x0806;
    private const string Disclosure =
        "Contains sanitized application logs and technical summaries only. No screen, clipboard, file content, credential, token, private key, password, full device ID, or personal document is included.";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ManifestProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "diagnosticId",
        "createdAtUtc",
        "expiresAtUtc",
        "appVersion",
        "operatingSystem",
        "architecture",
        "databaseSchemaVersion",
        "updateStatus",
        "connectionFailureCodes",
        "webRtcStatistics",
        "healthChecks",
        "disclosure",
    };
    private static readonly HashSet<string> AllowedArchitectures = new(StringComparer.Ordinal)
    {
        "X86", "X64", "Arm", "Arm64", "Wasm", "S390x", "LoongArch64", "Armv6", "Ppc64le",
    };
    private static readonly HashSet<string> AllowedStatisticNames = new(StringComparer.Ordinal)
    {
        "rttMs",
        "packetLossPercent",
        "jitterMs",
        "bitrateKbps",
        "captureFps",
        "encodeFps",
        "decodeFps",
        "renderFps",
        "droppedFrames",
        "reconnectCount",
    };
    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.Ordinal)
    {
        "password", "passwd", "secret", "token", "accesstoken", "refreshtoken", "authorization",
        "privatekey", "credential", "apikey", "accesskey", "jwt",
    };
    private static readonly HashSet<string> PersonalContentPropertyNames = new(StringComparer.Ordinal)
    {
        "screen", "screenshot", "screencontent", "clipboard", "clipboardcontent", "file", "filename",
        "filepath", "filecontent", "document", "documentcontent", "image", "imagecontent", "attachment",
        "username", "useremail", "emailaddress", "fullname",
    };

    public static async Task ValidateAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateCoreAsync(archivePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CloudServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or IOException
                                          or JsonException
                                          or DecoderFallbackException
                                          or ArgumentException
                                          or OverflowException
                                          or NotSupportedException)
        {
            throw Rejected("The diagnostic archive is malformed or contains unsupported content.", exception);
        }
    }

    private static async Task ValidateCoreAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > DiagnosticStorageOptions.MaximumArchiveBytes)
            throw Rejected("The diagnostic archive size is invalid.");

        var headers = await ReadAndValidateHeadersAsync(stream, cancellationToken);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, StrictUtf8);
        if (archive.Entries.Count != headers.Count)
            throw Rejected("The diagnostic archive directory is inconsistent.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expandedBytes = 0L;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries[index];
            var header = headers[index];
            ValidateEntryName(entry.FullName, index, seenNames);
            if (!string.Equals(entry.FullName, header.Name, StringComparison.Ordinal)
                || entry.Length != header.UncompressedLength
                || entry.CompressedLength != header.CompressedLength
                || unchecked((uint)entry.ExternalAttributes) != header.ExternalAttributes)
            {
                throw Rejected("The diagnostic archive directory is inconsistent.");
            }

            var maximumEntryBytes = index == 0 ? MaximumManifestBytes : MaximumLogBytes;
            if (entry.Length < 0 || entry.Length > maximumEntryBytes)
                throw Rejected("A diagnostic archive entry exceeds its size limit.");
            if (entry.Length > 0
                && (entry.CompressedLength <= 0
                    || entry.Length > entry.CompressedLength * MaximumCompressionRatio))
            {
                throw Rejected("A diagnostic archive entry exceeds the compression-ratio limit.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
                throw Rejected("The diagnostic archive exceeds the expanded-size limit.");

            var content = await ReadEntryAsync(entry, maximumEntryBytes, cancellationToken);
            if (ComputeCrc32(content) != header.Crc32)
                throw Rejected("A diagnostic archive entry failed its integrity check.");
            if (index == 0) ValidateManifest(content);
            else ValidateLog(content);
        }
    }

    private static async Task<List<CentralDirectoryEntry>> ReadAndValidateHeadersAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        const int endOfCentralDirectoryLength = 22;
        if (stream.Length < endOfCentralDirectoryLength)
            throw Rejected("The diagnostic archive has no central directory.");

        var footer = new byte[endOfCentralDirectoryLength];
        await ReadExactlyAtAsync(stream, stream.Length - footer.Length, footer, cancellationToken);
        if (ReadUInt32(footer, 0) != 0x06054b50
            || ReadUInt16(footer, 4) != 0
            || ReadUInt16(footer, 6) != 0
            || ReadUInt16(footer, 8) != ReadUInt16(footer, 10)
            || ReadUInt16(footer, 20) != 0)
        {
            throw Rejected("The diagnostic archive footer is unsupported.");
        }

        var entryCount = ReadUInt16(footer, 10);
        var centralDirectorySize = ReadUInt32(footer, 12);
        var centralDirectoryOffset = ReadUInt32(footer, 16);
        if (entryCount is < 1 or > MaximumEntries
            || centralDirectorySize is < 46 or > MaximumCentralDirectoryBytes
            || centralDirectoryOffset + centralDirectorySize != stream.Length - footer.Length)
        {
            throw Rejected("The diagnostic archive central directory is invalid.");
        }

        var directory = new byte[centralDirectorySize];
        await ReadExactlyAtAsync(stream, centralDirectoryOffset, directory, cancellationToken);
        var result = new List<CentralDirectoryEntry>(entryCount);
        var offset = 0;
        for (var index = 0; index < entryCount; index++)
        {
            if (directory.Length - offset < 46 || ReadUInt32(directory, offset) != 0x02014b50)
                throw Rejected("The diagnostic archive central directory is invalid.");

            var versionMadeBy = ReadUInt16(directory, offset + 4);
            var flags = ReadUInt16(directory, offset + 8);
            var compressionMethod = ReadUInt16(directory, offset + 10);
            var crc32 = ReadUInt32(directory, offset + 16);
            var compressedLength = ReadUInt32(directory, offset + 20);
            var uncompressedLength = ReadUInt32(directory, offset + 24);
            var nameLength = ReadUInt16(directory, offset + 28);
            var extraLength = ReadUInt16(directory, offset + 30);
            var commentLength = ReadUInt16(directory, offset + 32);
            var diskNumber = ReadUInt16(directory, offset + 34);
            var externalAttributes = ReadUInt32(directory, offset + 38);
            var localHeaderOffset = ReadUInt32(directory, offset + 42);
            var recordLength = checked(46 + nameLength + extraLength + commentLength);
            if (nameLength is < 1 or > MaximumEntryNameBytes
                || extraLength > MaximumExtraFieldBytes
                || extraLength != 0
                || commentLength != 0
                || diskNumber != 0
                || recordLength > directory.Length - offset)
            {
                throw Rejected("A diagnostic archive entry header is unsupported.");
            }
            ValidateFlagsAndCompression(flags, compressionMethod);
            ValidateExternalAttributes(versionMadeBy, externalAttributes);

            var name = StrictUtf8.GetString(directory, offset + 46, nameLength);
            result.Add(new CentralDirectoryEntry(
                name,
                flags,
                compressionMethod,
                crc32,
                compressedLength,
                uncompressedLength,
                externalAttributes,
                localHeaderOffset));
            offset += recordLength;
        }
        if (offset != directory.Length)
            throw Rejected("The diagnostic archive central directory contains hidden records.");

        var expectedLocalOffset = 0L;
        foreach (var entry in result)
        {
            if (entry.LocalHeaderOffset != expectedLocalOffset || entry.LocalHeaderOffset + 30 > centralDirectoryOffset)
                throw Rejected("The diagnostic archive contains hidden or overlapping data.");

            var localHeader = new byte[30];
            await ReadExactlyAtAsync(stream, entry.LocalHeaderOffset, localHeader, cancellationToken);
            if (ReadUInt32(localHeader, 0) != 0x04034b50)
                throw Rejected("A diagnostic archive local header is invalid.");
            var localFlags = ReadUInt16(localHeader, 6);
            var localCompressionMethod = ReadUInt16(localHeader, 8);
            var localNameLength = ReadUInt16(localHeader, 26);
            var localExtraLength = ReadUInt16(localHeader, 28);
            if (localFlags != entry.Flags
                || localCompressionMethod != entry.CompressionMethod
                || localNameLength is < 1 or > MaximumEntryNameBytes
                || localExtraLength > MaximumExtraFieldBytes
                || localExtraLength != 0)
            {
                throw Rejected("A diagnostic archive local header is inconsistent.");
            }
            ValidateFlagsAndCompression(localFlags, localCompressionMethod);

            var localName = new byte[localNameLength];
            await ReadExactlyAtAsync(stream, entry.LocalHeaderOffset + localHeader.Length, localName, cancellationToken);
            if (!string.Equals(StrictUtf8.GetString(localName), entry.Name, StringComparison.Ordinal))
                throw Rejected("A diagnostic archive entry name is inconsistent.");

            expectedLocalOffset = checked(
                (long)entry.LocalHeaderOffset + localHeader.Length + localNameLength + localExtraLength + entry.CompressedLength);
            if (expectedLocalOffset > centralDirectoryOffset)
                throw Rejected("A diagnostic archive entry overlaps its central directory.");
        }
        if (expectedLocalOffset != centralDirectoryOffset)
            throw Rejected("The diagnostic archive contains hidden data.");

        return result;
    }

    private static void ValidateFlagsAndCompression(ushort flags, ushort compressionMethod)
    {
        if ((flags & ~AllowedGeneralPurposeFlags) != 0
            || compressionMethod is not (0 or 8)
            || (compressionMethod == 0 && (flags & 0x0006) != 0))
        {
            throw Rejected("Encrypted or unsupported diagnostic archive entries are not accepted.");
        }
    }

    private static void ValidateExternalAttributes(ushort versionMadeBy, uint attributes)
    {
        var hostSystem = versionMadeBy >> 8;
        if (hostSystem == 3)
        {
            var unixFileType = (attributes >> 16) & 0xF000;
            if (unixFileType is not (0 or 0x8000))
                throw Rejected("Links and non-regular diagnostic archive entries are not accepted.");
        }

        var windowsAttributes = (FileAttributes)(attributes & 0xFFFF);
        if ((windowsAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw Rejected("Links and non-regular diagnostic archive entries are not accepted.");
    }

    private static void ValidateEntryName(string name, int index, HashSet<string> seenNames)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaximumEntryNameBytes
            || name[0] is '/' or '\\'
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains('\0', StringComparison.Ordinal)
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '.'))
            || name.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || !seenNames.Add(name))
        {
            throw Rejected("A diagnostic archive entry path is unsafe or duplicated.");
        }

        if (index == 0)
        {
            if (!string.Equals(name, "manifest.json", StringComparison.Ordinal))
                throw Rejected("The diagnostic manifest must be the first archive entry.");
            return;
        }

        var expected = $"logs/log-{index}.jsonl";
        if (!string.Equals(name, expected, StringComparison.Ordinal))
            throw Rejected("The diagnostic archive contains a non-allowlisted entry.");
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var content = new byte[checked((int)entry.Length)];
        await using var source = entry.Open();
        var total = 0;
        while (total < content.Length)
        {
            var read = await source.ReadAsync(content.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw Rejected("A diagnostic archive entry exceeds its size limit.");
        }

        var sentinel = new byte[1];
        var trailing = await source.ReadAsync(sentinel, cancellationToken);
        if (total != content.Length || trailing != 0)
            throw Rejected("A diagnostic archive entry length is inconsistent.");
        return content;
    }

    private static void ValidateManifest(byte[] content)
    {
        var text = DecodeUtf8(content);
        RejectSensitivePatterns(text);
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw Rejected("The diagnostic manifest must be a JSON object.");

        var properties = GetUniqueProperties(root);
        if (properties.Count != ManifestProperties.Count || ManifestProperties.Any(name => !properties.ContainsKey(name)))
            throw Rejected("The diagnostic manifest schema is invalid.");
        if (!properties["schemaVersion"].TryGetInt32(out var schemaVersion) || schemaVersion != 1)
            throw Rejected("The diagnostic manifest schema version is unsupported.");
        if (properties["diagnosticId"].ValueKind != JsonValueKind.String
            || !Guid.TryParse(properties["diagnosticId"].GetString(), out var bundleId)
            || bundleId == Guid.Empty)
        {
            throw Rejected("The diagnostic manifest identifier is invalid.");
        }
        if (!TryGetDateTimeOffset(properties["createdAtUtc"], out var createdAtUtc)
            || !TryGetDateTimeOffset(properties["expiresAtUtc"], out var expiresAtUtc)
            || expiresAtUtc <= createdAtUtc
            || expiresAtUtc - createdAtUtc > TimeSpan.FromDays(14))
        {
            throw Rejected("The diagnostic manifest lifetime is invalid.");
        }

        ValidateSafeString(properties["appVersion"], 64);
        ValidateSafeString(properties["operatingSystem"], 256);
        var architecture = ValidateSafeString(properties["architecture"], 32);
        if (!AllowedArchitectures.Contains(architecture))
            throw Rejected("The diagnostic manifest architecture is invalid.");
        if (!properties["databaseSchemaVersion"].TryGetInt32(out var databaseSchemaVersion)
            || databaseSchemaVersion is < 0 or > 1_000_000)
        {
            throw Rejected("The diagnostic manifest database schema version is invalid.");
        }
        ValidateSafeString(properties["updateStatus"], 80);
        ValidateCodeArray(properties["connectionFailureCodes"], 50, 64);
        ValidateStatistics(properties["webRtcStatistics"]);
        ValidateHealthChecks(properties["healthChecks"]);
        if (!string.Equals(ValidateSafeString(properties["disclosure"], Disclosure.Length), Disclosure, StringComparison.Ordinal))
            throw Rejected("The diagnostic manifest disclosure is invalid.");
    }

    private static void ValidateLog(byte[] content)
    {
        var text = DecodeUtf8(content);
        RejectSensitivePatterns(text);
        var mode = LogLineMode.Unknown;
        var lineCount = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (++lineCount > 50_000 || line.Length > 128 * 1024)
                throw Rejected("A diagnostic log exceeds its structural limits.");
            if (string.IsNullOrWhiteSpace(line)) continue;

            var currentMode = line.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal)
                ? LogLineMode.Json
                : LogLineMode.PlainText;
            if (mode == LogLineMode.Unknown) mode = currentMode;
            else if (mode != currentMode)
                throw Rejected("A diagnostic log mixes incompatible record formats.");

            if (currentMode == LogLineMode.Json) ValidateJsonLogLine(line);
            else ValidatePrintableText(line);
        }
    }

    private static void ValidateJsonLogLine(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Rejected("A diagnostic JSON log record must be an object.");
        var nodeCount = 0;
        ValidateJsonValue(document.RootElement, ref nodeCount);
    }

    private static void ValidateJsonValue(JsonElement value, ref int nodeCount)
    {
        if (++nodeCount > 10_000)
            throw Rejected("A diagnostic JSON log record is too complex.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw Rejected("A diagnostic JSON log record contains duplicate properties.");
                    ValidatePrintableText(property.Name);
                    var normalizedName = NormalizePropertyName(property.Name);
                    if ((SecretPropertyNames.Contains(normalizedName) || PersonalContentPropertyNames.Contains(normalizedName))
                        && !IsRedactedOrEmpty(property.Value))
                    {
                        throw Rejected("A diagnostic log contains a forbidden sensitive field.");
                    }
                    ValidateJsonValue(property.Value, ref nodeCount);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateJsonValue(item, ref nodeCount);
                break;
            case JsonValueKind.String:
                ValidatePrintableText(value.GetString() ?? string.Empty);
                RejectSensitivePatterns(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (value.TryGetDouble(out var number) && !double.IsFinite(number))
                    throw Rejected("A diagnostic JSON log record contains a non-finite number.");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw Rejected("A diagnostic JSON log record contains an unsupported value.");
        }
    }

    private static Dictionary<string, JsonElement> GetUniqueProperties(JsonElement value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value))
                throw Rejected("The diagnostic manifest contains duplicate properties.");
        }
        return result;
    }

    private static void ValidateCodeArray(JsonElement value, int maximumCount, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount)
            throw Rejected("A diagnostic manifest code list is invalid.");
        foreach (var item in value.EnumerateArray())
        {
            var code = ValidateSafeString(item, maximumLength);
            if (!IsCode(code)) throw Rejected("A diagnostic manifest code is invalid.");
        }
    }

    private static void ValidateStatistics(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Rejected("The diagnostic statistics summary is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)
                || names.Count > AllowedStatisticNames.Count
                || !AllowedStatisticNames.Contains(property.Name)
                || property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out var number)
                || !double.IsFinite(number))
            {
                throw Rejected("The diagnostic statistics summary is invalid.");
            }
        }
    }

    private static void ValidateHealthChecks(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Rejected("The diagnostic health-check summary is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)
                || names.Count > 30
                || property.Name.Length is < 1 or > 48
                || !IsCode(property.Name))
            {
                throw Rejected("The diagnostic health-check summary is invalid.");
            }
            var status = ValidateSafeString(property.Value, 48);
            if (!IsCode(status)) throw Rejected("The diagnostic health-check summary is invalid.");
        }
    }

    private static string ValidateSafeString(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Rejected("A diagnostic manifest string field is invalid.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length > maximumLength)
            throw Rejected("A diagnostic manifest string field exceeds its limit.");
        ValidatePrintableText(text);
        RejectSensitivePatterns(text);
        return text;
    }

    private static bool TryGetDateTimeOffset(JsonElement value, out DateTimeOffset result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out result);
    }

    private static bool IsCode(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static string DecodeUtf8(byte[] content)
    {
        var text = StrictUtf8.GetString(content);
        if (text.Length > 0 && text[0] == '\uFEFF')
            throw Rejected("Diagnostic archive text must not contain a byte-order mark.");
        ValidatePrintableText(text, allowLineBreaks: true);
        return text;
    }

    private static void ValidatePrintableText(string value, bool allowLineBreaks = false)
    {
        foreach (var character in value)
        {
            if (character == '\0'
                || (char.IsControl(character)
                    && !(allowLineBreaks && character is '\r' or '\n' or '\t')))
            {
                throw Rejected("Diagnostic archive text contains binary or control data.");
            }
        }
    }

    private static void RejectSensitivePatterns(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (FullDeviceIdPattern().IsMatch(value)
            || Ipv4Pattern().IsMatch(value)
            || JwtPattern().IsMatch(value)
            || BearerTokenPattern().IsMatch(value)
            || PrivateKeyPattern().IsMatch(value)
            || WindowsPathPattern().IsMatch(value)
            || UnixPersonalPathPattern().IsMatch(value)
            || EmailPattern().IsMatch(value)
            || HasUnredactedAssignment(SecretAssignmentPattern(), value)
            || HasUnredactedAssignment(PersonalContentAssignmentPattern(), value))
        {
            throw Rejected("The diagnostic archive contains a forbidden sensitive pattern.");
        }

        foreach (Match match in PossibleIpv6Pattern().Matches(value))
        {
            if (IPAddress.TryParse(match.Value, out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                throw Rejected("The diagnostic archive contains a forbidden sensitive pattern.");
            }
        }
    }

    private static bool HasUnredactedAssignment(Regex pattern, string value)
    {
        foreach (Match match in pattern.Matches(value))
        {
            var captured = match.Groups["value"].Value;
            if (!captured.StartsWith("[redacted", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsRedactedOrEmpty(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return string.IsNullOrEmpty(text) || text.StartsWith("[redacted", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePropertyName(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task ReadExactlyAtAsync(
        FileStream stream,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        await stream.ReadExactlyAsync(destination, cancellationToken);
    }

    private static ushort ReadUInt16(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));

    private static uint ComputeCrc32(ReadOnlySpan<byte> value)
    {
        var crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static CloudServiceException Rejected(string message, Exception? innerException = null) =>
        new(CloudErrorCodes.DiagnosticUploadRejected, message, innerException: innerException);

    [GeneratedRegex(@"(?<!\d)(?:LNK-)?\d{3}-\d{3}-\d{3}-\d{3}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex FullDeviceIdPattern();

    [GeneratedRegex(@"(?<![\w])(?:\d{1,3}\.){3}\d{1,3}(?![\w])")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![A-Fa-f0-9:])(?:[A-Fa-f0-9]{0,4}:){2,7}[A-Fa-f0-9]{0,4}(?![A-Fa-f0-9:])")]
    private static partial Regex PossibleIpv6Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n\""<>|]+")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?i)(?:^|[\s\""'=])/(?:home|users|etc|root|var|tmp)/[^\s\""']+")]
    private static partial Regex UnixPersonalPathPattern();

    [GeneratedRegex(@"(?i)(?<![\w.+-])[\w.+-]+@[a-z0-9.-]+\.[a-z]{2,}(?![\w.-])")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)(?:password|passwd|secret|token|access[_ -]?token|refresh[_ -]?token|authorization|private[_ -]?key|credential|api[_ -]?key|access[_ -]?key|jwt)[\""']?\s*[:=]\s*[\""']?(?<value>[^\""'\s,;}\]]+)")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"(?i)(?:screen(?:shot|\s+content)?|clipboard(?:\s+content)?|file(?:name|path|\s+content)?|document(?:\s+content)?|image(?:\s+content)?|attachment|user(?:name|\s+email)|email(?:\s+address)?|full\s+name)[\""']?\s*[:=]\s*[\""']?(?<value>[^\""'\s,;}\]]+)")]
    private static partial Regex PersonalContentAssignmentPattern();

    private enum LogLineMode
    {
        Unknown,
        PlainText,
        Json,
    }

    private sealed record CentralDirectoryEntry(
        string Name,
        ushort Flags,
        ushort CompressionMethod,
        uint Crc32,
        uint CompressedLength,
        uint UncompressedLength,
        uint ExternalAttributes,
        uint LocalHeaderOffset);
}

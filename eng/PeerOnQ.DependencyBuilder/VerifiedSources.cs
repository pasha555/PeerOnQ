using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PeerOnQ.DependencyBuilder;

internal static partial class VerifiedSources
{
    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void RequireHash(string path, string expected)
    {
        if (!HashPattern().IsMatch(expected) || !string.Equals(Sha(path), expected, StringComparison.Ordinal))
            throw new InvalidDataException($"SHA256 mismatch: {Path.GetFileName(path)}");
    }

    internal static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.StartsWith('/') || relative.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException("Unsafe source archive path.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Source archive path escaped extraction root.");
        return result;
    }

    internal static async Task ExtractAsync(string archive, string destination, string expectedPrefix, CancellationToken token)
    {
        await using var input = File.OpenRead(archive);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        long extracted = 0;
        var entries = 0;
        var prefix = expectedPrefix.TrimEnd('/') + "/";
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (++entries > 20_000) throw new InvalidDataException("Source archive has too many entries.");
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes) continue;
            if (entry.Name.TrimEnd('/') == expectedPrefix && entry.EntryType == TarEntryType.Directory) continue;
            if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected source archive root.");
            var relative = entry.Name[prefix.Length..].TrimEnd('/');
            var target = ContainedPath(destination, relative);
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Links and special files are forbidden in the source archive.");
            if (entry.Length is < 0 or > 128 * 1024 * 1024 || (extracted = checked(extracted + entry.Length)) > 512L * 1024 * 1024)
                throw new InvalidDataException("Source archive exceeds extraction bounds.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token);
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated source archive entry.");
        }
    }

    internal static byte[] ApplyPatch(byte[] original, byte[] patch, string targetPath)
    {
        if (original.Length > 1024 * 1024 || patch.Length > 64 * 1024)
            throw new InvalidDataException("Patch exceeds supported bounds.");
        // Preserve BOM and line endings as data. The pinned patch/output hashes are authoritative.
        var source = Encoding.UTF8.GetString(original).Split('\n');
        var lines = Encoding.UTF8.GetString(patch).Split('\n');
        if (lines.Count(line => line.StartsWith("--- ", StringComparison.Ordinal)) != 1
            || lines.Count(line => line.StartsWith("+++ ", StringComparison.Ordinal)) != 1
            || !lines.Contains("--- a/" + targetPath) || !lines.Contains("+++ b/" + targetPath))
            throw new InvalidDataException("Patch must modify exactly the declared source file.");
        var output = new List<string>();
        var position = 0;
        var hunks = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal)) continue;
            var header = HunkPattern().Match(lines[index]);
            if (!header.Success) throw new InvalidDataException("Invalid patch hunk header.");
            hunks++;
            var start = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
            var oldCount = int.Parse(header.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var newCount = int.Parse(header.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (start < position || start > source.Length) throw new InvalidDataException("Overlapping patch hunks.");
            while (position < start) output.Add(source[position++]);
            var consumed = 0;
            var produced = 0;
            while (++index < lines.Length && !lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var line = lines[index];
                if (line.Length == 0 && index == lines.Length - 1) break;
                if (line.Length == 0 || line[0] is not (' ' or '-' or '+'))
                    throw new InvalidDataException("Unsupported patch content.");
                var value = line[1..];
                if (line[0] is ' ' or '-')
                {
                    if (position >= source.Length || source[position++] != value)
                        throw new InvalidDataException("Patch context does not match pinned source.");
                    consumed++;
                }
                if (line[0] is ' ' or '+') { output.Add(value); produced++; }
            }
            if (consumed != oldCount || produced != newCount)
                throw new InvalidDataException("Patch hunk counts do not match.");
            index--;
        }
        if (hunks == 0) throw new InvalidDataException("Patch contains no changes.");
        while (position < source.Length) output.Add(source[position++]);
        return new UTF8Encoding(false).GetBytes(string.Join('\n', output));
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    [GeneratedRegex("^@@ -(\\d+),(\\d+) \\+(\\d+),(\\d+) @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkPattern();
}

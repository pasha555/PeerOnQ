namespace PeerOnQ.Application.Collaboration;

public static class SafeTransferPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] WindowsInvalidCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    public static string NormalizeRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.IndexOf('\0') >= 0)
            throw new InvalidDataException("The transfer path is empty or invalid.");

        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException("Absolute transfer paths are not allowed.");

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException("Transfer path traversal is not allowed.");

        foreach (var part in parts)
        {
            if (part.Length > 200
                || part.EndsWith(' ')
                || part.EndsWith('.')
                || part.Any(character => character < 32 || WindowsInvalidCharacters.Contains(character)))
            {
                throw new InvalidDataException("A transfer path segment is invalid on Windows.");
            }

            var baseName = part.Split('.', 2)[0];
            if (ReservedNames.Contains(baseName))
                throw new InvalidDataException("A reserved Windows device name is not allowed.");
        }

        var result = string.Join(Path.DirectorySeparatorChar, parts);
        if (result.Length > 220)
            throw new PathTooLongException("The transfer path exceeds the safe length limit.");
        return result;
    }

    public static string ResolveUnderRoot(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var normalized = NormalizeRelative(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transfer path leaves the selected destination.");

        RejectReparsePoints(root, candidate);
        return candidate;
    }

    public static void RejectReparsePoints(string rootDirectory, string candidate)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Reparse-point destinations are not allowed.");

        var current = Path.GetDirectoryName(candidate);
        while (!string.IsNullOrEmpty(current)
               && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("A destination parent is a reparse point.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    public static bool IsReparsePoint(string path) =>
        (File.Exists(path) || Directory.Exists(path))
        && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public static string ResolveCollision(string path, TransferCollisionPolicy policy)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        return policy switch
        {
            TransferCollisionPolicy.Overwrite => path,
            TransferCollisionPolicy.Skip => string.Empty,
            TransferCollisionPolicy.Rename => NextAvailableName(path),
            _ => throw new IOException("A collision decision is required before accepting the transfer."),
        };
    }

    private static string NextAvailableName(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);

        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("No collision-free destination name is available.");
    }
}

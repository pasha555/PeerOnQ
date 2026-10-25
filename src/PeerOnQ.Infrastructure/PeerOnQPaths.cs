using PeerOnQ.Infrastructure.Compatibility;

namespace PeerOnQ.Infrastructure;

/// <summary>Every file PeerOnQ writes lives under one per-user directory.</summary>
public sealed class PeerOnQPaths
{
    private const string MigrationMarkerFileName = ".peeronq-data-migration-v1";
    private readonly string? _legacyRoot;

    public PeerOnQPaths(string? rootOverride = null, string? legacyRootOverride = null)
    {
        if (rootOverride is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Root = Path.Combine(localAppData, "PeerOnQ");
            _legacyRoot = legacyRootOverride ?? Path.Combine(localAppData, LegacyBrandCompatibility.DataDirectoryName);
        }
        else
        {
            Root = Path.GetFullPath(rootOverride);
            _legacyRoot = legacyRootOverride is null ? null : Path.GetFullPath(legacyRootOverride);
        }

        DatabaseFile = Path.Combine(Root, "peeronq.db");
        ConnectionSettingsFile = Path.Combine(Root, "connection.json");
        SecretsDirectory = Path.Combine(Root, "secrets");
        LogDirectory = Path.Combine(Root, "logs");
        UpdateDirectory = Path.Combine(Root, "updates");
        CrashReportDirectory = Path.Combine(Root, "crash-reports");
        DiagnosticsDirectory = Path.Combine(Root, "diagnostics");
    }

    public string Root { get; }
    public string DatabaseFile { get; }
    public string ConnectionSettingsFile { get; }
    public string SecretsDirectory { get; }
    public string LogDirectory { get; }
    public string UpdateDirectory { get; }
    public string CrashReportDirectory { get; }
    public string DiagnosticsDirectory { get; }

    public PeerOnQPaths EnsureCreated()
    {
        MigrateLegacyDataDirectory();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UpdateDirectory);
        Directory.CreateDirectory(CrashReportDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        return this;
    }

    private void MigrateLegacyDataDirectory()
    {
        if (string.IsNullOrWhiteSpace(_legacyRoot)
            || string.Equals(Root, _legacyRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(_legacyRoot))
        {
            return;
        }

        var markerPath = Path.Combine(Root, MigrationMarkerFileName);
        if (File.Exists(markerPath)) return;

        if (Directory.Exists(Root))
        {
            if (Directory.EnumerateFileSystemEntries(Root).Any())
            {
                throw new InvalidOperationException(
                    "PeerOnQ and legacy application data both exist. Migration stopped to protect the existing device identity.");
            }

            Directory.Delete(Root);
        }

        var parent = Path.GetDirectoryName(Root)
            ?? throw new InvalidOperationException("The PeerOnQ data directory has no parent directory.");
        Directory.CreateDirectory(parent);
        var stagingRoot = Path.Combine(parent, $".peeronq-migration-{Guid.NewGuid():N}");

        try
        {
            CopyDirectory(_legacyRoot, stagingRoot);
            RenameLegacyDatabaseFiles(stagingRoot);
            RenameLegacyLogFiles(stagingRoot);
            File.WriteAllText(Path.Combine(stagingRoot, MigrationMarkerFileName), "completed");

            try
            {
                Directory.Move(stagingRoot, Root);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                // A second PeerOnQ process completed the same idempotent migration first.
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Legacy application data cannot be migrated through a reparse point.");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
        }

        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Legacy application data contains an unsupported reparse point.");
            }

            CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name));
        }
    }

    private static void RenameLegacyDatabaseFiles(string root)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var legacyPath = Path.Combine(root, $"{LegacyBrandCompatibility.DatabaseFileName}{suffix}");
            var peerOnQPath = Path.Combine(root, $"peeronq.db{suffix}");
            if (!File.Exists(legacyPath)) continue;
            if (File.Exists(peerOnQPath))
            {
                throw new InvalidOperationException("Legacy data contains conflicting database files.");
            }

            File.Move(legacyPath, peerOnQPath);
        }
    }

    private static void RenameLegacyLogFiles(string root)
    {
        var logDirectory = Path.Combine(root, "logs");
        if (!Directory.Exists(logDirectory)) return;

        foreach (var legacyPath in Directory.EnumerateFiles(logDirectory, LegacyBrandCompatibility.LogFilePattern))
        {
            var legacyName = Path.GetFileName(legacyPath);
            var peerOnQName = "peeronq-" + legacyName[LegacyBrandCompatibility.LogFilePrefix.Length..];
            var peerOnQPath = Path.Combine(logDirectory, peerOnQName);
            if (!File.Exists(peerOnQPath)) File.Move(legacyPath, peerOnQPath);
        }
    }
}

using System.Diagnostics;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.Activation;

namespace PeerOnQ.App;

// Fully qualified base type: the PeerOnQ.Application namespace would otherwise shadow
// Microsoft.UI.Xaml.Application inside the PeerOnQ.App namespace.
public partial class App : Microsoft.UI.Xaml.Application
{
    private const string PortableSupportMarkerFileName = "PeerOnQ.PortableSupport.json";
    private const string DesktopInstanceKey = "PeerOnQ.Desktop.v1";
    private const string PortableSupportInstanceKey = "PeerOnQ.PortableSupport.v1";
    private static readonly long StartupTimestamp = Stopwatch.GetTimestamp();
    private static long _readyTimestamp;
    private static string? _portableDataDirectory;
    private static FileStream? _portableOwnershipLock;
    private AppInstance? _primaryInstance;
    private string? _pendingSupportUri;
    private int _pendingSecondaryActivation;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => RecordCrash(args.Exception, "winui_unhandled");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RecordCrash(args.ExceptionObject as Exception ?? new InvalidOperationException("Unknown unhandled exception."), "appdomain_unhandled");
        TaskScheduler.UnobservedTaskException += (_, args) => RecordCrash(args.Exception, "task_unobserved");
    }

    public static MainWindow? Main { get; private set; }

    public static AppServices? Services { get; internal set; }

    public static bool IsPortableSupport { get; private set; }

    public static TimeSpan StartupElapsed => Stopwatch.GetElapsedTime(
        StartupTimestamp,
        Volatile.Read(ref _readyTimestamp) is var ready && ready > 0 ? ready : Stopwatch.GetTimestamp());

    internal static void MarkReady() => Interlocked.CompareExchange(ref _readyTimestamp, Stopwatch.GetTimestamp(), 0);

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var currentInstance = AppInstance.GetCurrent();
        var instanceKey = IsPortableSupportRequested()
            ? PortableSupportInstanceKey
            : DesktopInstanceKey;
        var primaryInstance = AppInstance.FindOrRegisterForKey(instanceKey);
        if (!primaryInstance.IsCurrent)
        {
            await primaryInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }

        _primaryInstance = primaryInstance;
        _primaryInstance.Activated += OnInstanceActivated;
        PreparePortableSupportMode();
        Main = new MainWindow();
        var supportUri = FindSupportUri(currentInstance.GetActivatedEventArgs())
                         ?? FindSupportUri(Environment.GetCommandLineArgs().Skip(1));
        if (supportUri is not null) Main.ApplySupportInvitationUri(supportUri);
        Main.Activate();

        if (Interlocked.Exchange(ref _pendingSecondaryActivation, 0) != 0)
        {
            Main.ActivateFromSecondaryLaunch(Interlocked.Exchange(ref _pendingSupportUri, null));
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        var supportUri = FindSupportUri(args);
        if (supportUri is not null) Interlocked.Exchange(ref _pendingSupportUri, supportUri);
        Interlocked.Exchange(ref _pendingSecondaryActivation, 1);

        if (Main is { } main)
        {
            Interlocked.Exchange(ref _pendingSecondaryActivation, 0);
            main.ActivateFromSecondaryLaunch(Interlocked.Exchange(ref _pendingSupportUri, null));
        }
    }

    private static string? FindSupportUri(AppActivationArguments args) => args.Data switch
    {
        IProtocolActivatedEventArgs protocol => FindSupportUri([protocol.Uri.AbsoluteUri]),
        ILaunchActivatedEventArgs launch => FindSupportUri([launch.Arguments]),
        _ => null,
    };

    private static string? FindSupportUri(IEnumerable<string> arguments) => arguments
        .Select(value => value.Trim().Trim('"'))
        .FirstOrDefault(value => value.StartsWith("peeronq://support?", StringComparison.OrdinalIgnoreCase));

    internal static void CleanupPortableSupportData()
    {
        var directory = Interlocked.Exchange(ref _portableDataDirectory, null);
        if (directory is null) return;

        try
        {
            Interlocked.Exchange(ref _portableOwnershipLock, null)?.Dispose();
            var portableRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerOnQ", "PortableSupport"));
            var expectedPrefix = portableRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedDirectory = Path.GetFullPath(directory);
            if (!resolvedDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return;

            if (Directory.Exists(resolvedDirectory)) Directory.Delete(resolvedDirectory, recursive: true);
            if (string.Equals(
                    Environment.GetEnvironmentVariable(AppServices.DataDirectoryEnvironmentVariableName),
                    resolvedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(AppServices.DataDirectoryEnvironmentVariableName, null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PeerOnQ portable profile cleanup failed: {ex.GetType().Name}");
        }
    }

    private static void PreparePortableSupportMode()
    {
        if (!IsPortableSupportRequested()) return;

        var portableRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerOnQ", "PortableSupport"));
        Directory.CreateDirectory(portableRoot);
        CleanupAbandonedPortableProfiles(portableRoot);
        var dataDirectory = Path.Combine(
            portableRoot,
            $"p{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        _portableOwnershipLock = new FileStream(
            Path.Combine(dataDirectory, ".owner.lock"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        Environment.SetEnvironmentVariable(AppServices.DataDirectoryEnvironmentVariableName, dataDirectory);
        _portableDataDirectory = dataDirectory;
        IsPortableSupport = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupPortableSupportData();
    }

    private static bool IsPortableSupportRequested() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(value => value.Equals("--portable-support", StringComparison.OrdinalIgnoreCase))
        || File.Exists(Path.Combine(AppContext.BaseDirectory, PortableSupportMarkerFileName));

    private static void CleanupAbandonedPortableProfiles(string portableRoot)
    {
        foreach (var candidate in Directory.EnumerateDirectories(portableRoot))
        {
            try
            {
                var resolved = Path.GetFullPath(candidate);
                var expectedPrefix = portableRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) continue;
                var processBoundProfile = TryGetPortableOwnerProcessId(Path.GetFileName(resolved), out var ownerProcessId);
                if (processBoundProfile)
                {
                    try
                    {
                        using var ownerProcess = Process.GetProcessById(ownerProcessId);
                        if (!ownerProcess.HasExited) continue;
                    }
                    catch (ArgumentException)
                    {
                        // The owning process has exited, so its ephemeral profile is abandoned.
                    }
                }

                var ownerLock = Path.Combine(resolved, ".owner.lock");
                if (!File.Exists(ownerLock))
                {
                    if (!processBoundProfile
                        && Directory.GetLastWriteTimeUtc(resolved) > DateTime.UtcNow.AddDays(-1)) continue;
                }
                else
                {
                    using var abandonedLock = new FileStream(
                        ownerLock,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                Directory.Delete(resolved, recursive: true);
            }
            catch (IOException)
            {
                // A live portable process owns this profile or a file is still being released.
            }
            catch (UnauthorizedAccessException)
            {
                // Fail closed instead of changing permissions or following a foreign path.
            }
        }
    }

    private static bool TryGetPortableOwnerProcessId(string directoryName, out int processId)
    {
        processId = 0;
        var separator = directoryName.IndexOf('-');
        return separator > 1
               && directoryName[0] == 'p'
               && int.TryParse(
                   directoryName.AsSpan(1, separator - 1),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out processId)
               && processId > 0;
    }

    private static void RecordCrash(Exception exception, string source)
    {
        var reports = Services?.CrashReports;
        if (reports is null) return;
        _ = Task.Run(async () =>
        {
            try { await reports.RecordAsync(exception, source); }
            catch { /* Crash reporting must never cause a second process failure. */ }
        });
    }
}

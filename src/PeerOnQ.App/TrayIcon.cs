using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace PeerOnQ.App;

/// <summary>
/// Notification-area status indicator built directly on Shell_NotifyIcon because WinUI 3 has no
/// tray control. It makes active-session state visible outside the main window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x00000010;
    private const int LrDefaultSize = 0x00000040;
    private const uint CallbackMessage = 0x8000 + 0x51;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmHotKey = 0x0312;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint EndSessionCommand = 1;
    private const uint RevokeControlCommand = 2;
    private const uint SettingsCommand = 3;
    private const uint ExitCommand = 4;
    private const int EmergencyStopHotKeyId = 0x504F;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyF12 = 0x7B;
    private const nuint SubclassId = 0x504F51;

    private NotifyIconData _data;
    private readonly nint _brandIcon;
    private readonly nint _ownerWindow;
    private readonly Func<Task> _onEndSession;
    private readonly Func<Task> _onRevokeControl;
    private readonly Action _onOpenSettings;
    private readonly Action _onExit;
    private readonly SubclassProcedure _subclassProcedure;
    private bool _created;
    private bool _subclassInstalled;
    private bool _hotKeyRegistered;
    private bool _disposed;
    private int _active;
    private int _canRevokeControl;
    private int _ending;
    private int _revoking;
    private int _exiting;

    public TrayIcon(
        nint ownerWindow,
        string tooltip,
        Func<Task> onEndSession,
        Func<Task> onRevokeControl,
        Action onOpenSettings,
        Action onExit)
    {
        if (ownerWindow == nint.Zero)
            throw new ArgumentException("A PeerOnQ-owned top-level window is required.", nameof(ownerWindow));

        _ownerWindow = ownerWindow;
        _onEndSession = onEndSession;
        _onRevokeControl = onRevokeControl;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;
        _subclassProcedure = OnOwnerWindowMessage;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PeerOnQ.ico");
        _brandIcon = LoadImage(
            nint.Zero,
            iconPath,
            ImageIcon,
            0,
            0,
            LrLoadFromFile | LrDefaultSize);
        if (_brandIcon == nint.Zero)
        {
            throw new InvalidOperationException($"PeerOnQ tray icon could not be loaded from '{iconPath}'.");
        }

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = ownerWindow,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = unchecked((int)CallbackMessage),
            hIcon = _brandIcon,
            szTip = tooltip,
        };

        _subclassInstalled = SetWindowSubclass(
            ownerWindow,
            _subclassProcedure,
            SubclassId,
            nint.Zero);
        _created = Shell_NotifyIcon(NimAdd, ref _data);
        if (!_created && _subclassInstalled)
        {
            RemoveWindowSubclass(ownerWindow, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }
        if (_created && _subclassInstalled)
        {
            _hotKeyRegistered = RegisterHotKey(
                ownerWindow,
                EmergencyStopHotKeyId,
                ModControl | ModAlt | ModShift | ModNoRepeat,
                VirtualKeyF12);
        }
    }

    /// <summary>True when the tray can provide the local owner with an immediate stop command.</summary>
    public bool SupportsSessionEndCommand => _created && _subclassInstalled && !_disposed;
    public bool SupportsEmergencyStopShortcut => _hotKeyRegistered && !_disposed;

    /// <summary>Updates the tooltip while retaining the PeerOnQ tray identity.</summary>
    public void SetActiveSession(bool active, string tooltip, bool canRevokeControl = false)
    {
        if (_disposed || !_created) return;

        Volatile.Write(ref _active, active ? 1 : 0);
        Volatile.Write(ref _canRevokeControl, active && canRevokeControl ? 1 : 0);
        _data.hIcon = _brandIcon;
        _data.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        Shell_NotifyIcon(NimModify, ref _data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_created)
        {
            Shell_NotifyIcon(NimDelete, ref _data);
            _created = false;
        }

        if (_subclassInstalled)
        {
            if (_hotKeyRegistered)
            {
                UnregisterHotKey(_ownerWindow, EmergencyStopHotKeyId);
                _hotKeyRegistered = false;
            }
            RemoveWindowSubclass(_ownerWindow, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }

        DestroyIcon(_brandIcon);
    }

    private nint OnOwnerWindowMessage(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (mouseMessage is WmContextMenu or WmRButtonUp)
            {
                ShowContextMenu();
                return nint.Zero;
            }
        }
        else if (message == WmHotKey && wParam.ToInt32() == EmergencyStopHotKeyId)
        {
            RequestEndSession();
            return nint.Zero;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero) return;

        try
        {
            var active = Volatile.Read(ref _active) != 0;
            var canRevoke = active && Volatile.Read(ref _canRevokeControl) != 0;
            AppendMenu(menu, MfString, SettingsCommand, "Settings");
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(
                menu,
                MfString | (canRevoke ? 0u : MfGrayed),
                RevokeControlCommand,
                "Revoke remote control");
            AppendMenu(
                menu,
                MfString | (active ? 0u : MfGrayed),
                EndSessionCommand,
                "End current session");
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString, ExitCommand, "Exit");

            if (!GetCursorPos(out var cursor)) return;
            SetForegroundWindow(_ownerWindow);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmNonotify | TpmReturnCmd,
                cursor.X,
                cursor.Y,
                0,
                _ownerWindow,
                nint.Zero);
            if (command == SettingsCommand)
            {
                RequestOpenSettings();
            }
            else if (command == RevokeControlCommand && canRevoke)
            {
                RequestRevokeControl();
            }
            else if (command == EndSessionCommand && active)
            {
                RequestEndSession();
            }
            else if (command == ExitCommand)
            {
                RequestExit();
            }
        }
        finally
        {
            DestroyMenu(menu);
            PostMessage(_ownerWindow, WmNull, nint.Zero, nint.Zero);
        }
    }

    private void RequestOpenSettings()
    {
        try
        {
            _onOpenSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "PeerOnQ tray settings command failed ({0}).",
                ex.GetType().Name);
        }
    }

    private void RequestExit()
    {
        if (Interlocked.CompareExchange(ref _exiting, 1, 0) != 0) return;

        try
        {
            _onExit();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _exiting, 0);
            Trace.TraceWarning(
                "PeerOnQ tray exit command failed ({0}).",
                ex.GetType().Name);
        }
    }

    private void RequestEndSession()
    {
        if (Volatile.Read(ref _active) == 0
            || Interlocked.CompareExchange(ref _ending, 1, 0) != 0)
            return;

        _ = EndSessionAsync();
    }

    private void RequestRevokeControl()
    {
        if (Volatile.Read(ref _canRevokeControl) == 0
            || Interlocked.CompareExchange(ref _revoking, 1, 0) != 0)
            return;

        _ = RevokeControlAsync();
    }

    private async Task RevokeControlAsync()
    {
        try
        {
            await _onRevokeControl();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "PeerOnQ tray revoke-control command failed ({0}).",
                ex.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _revoking, 0);
        }
    }

    private async Task EndSessionAsync()
    {
        try
        {
            await _onEndSession();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "PeerOnQ tray end-session command failed ({0}).",
                ex.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _ending, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        int type,
        int desiredWidth,
        int desiredHeight,
        int loadFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string itemText);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint ownerWindow,
        nint reservedRectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}

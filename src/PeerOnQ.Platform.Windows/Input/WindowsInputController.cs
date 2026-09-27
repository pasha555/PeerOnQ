using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Platform.Windows.Capture;

namespace PeerOnQ.Platform.Windows.Input;

/// <summary>
/// Applies explicitly authorized remote input with the supported Windows SendInput API.
/// UIPI and the secure desktop remain OS-enforced boundaries: this asInvoker process cannot
/// control elevated applications or synthesize the secure-attention sequence.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsInputController(ILogger<WindowsInputController>? logger = null) : IRemoteInputSink
{
    private readonly Lock _gate = new();
    private readonly ILogger _log = logger ?? NullLogger<WindowsInputController>.Instance;
    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<RemotePointerButton> _pressedButtons = [];
    private DisplayBounds? _target;
    private bool _enabled;
    private bool _successLogged;

    public void SetCaptureTarget(CaptureTargetInfo? target)
    {
        lock (_gate)
        {
            _enabled = false;
            ReleaseAllCore();
            _target = target is null ? null : ResolveBounds(target);
        }
    }

    public void RestoreApprovedScope(SessionPermission approvedPermissions)
    {
        lock (_gate)
        {
            var enable = approvedPermissions.HasFlag(SessionPermission.ControlInput) && _target is not null;
            if (enable && !ReleaseAllCore()) enable = false;
            if (enable && !_enabled) _successLogged = false;
            _enabled = enable;
            if (!_enabled) ReleaseAllCore();
        }
    }

    public void DisableAndReleaseAll()
    {
        lock (_gate)
        {
            _enabled = false;
            ReleaseAllCore();
        }
    }

    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllCore();
    }

    public bool TryInject(RemoteInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsValid()) return false;

        lock (_gate)
        {
            if (!_enabled) return false;

            if (input.Kind == RemoteInputEventKind.Key
                && input.IsPressed
                && input.VirtualKey == VirtualKeyDelete
                && IsPressed(VirtualKeyControl, VirtualKeyLeftControl, VirtualKeyRightControl)
                && IsPressed(VirtualKeyMenu, VirtualKeyLeftMenu, VirtualKeyRightMenu))
            {
                _enabled = false;
                ReleaseAllCore();
                _log.LogWarning("Rejected a secure-attention key combination; remote input was disabled");
                return false;
            }

            var commands = new List<NativeInput>(2);
            if (input.Kind is RemoteInputEventKind.PointerMove
                or RemoteInputEventKind.PointerButton
                or RemoteInputEventKind.PointerWheel)
            {
                if (_target is not { } target) return false;
                commands.Add(CreateAbsoluteMove(input.NormalizedX, input.NormalizedY, target));
            }

            switch (input.Kind)
            {
                case RemoteInputEventKind.PointerMove:
                    break;
                case RemoteInputEventKind.PointerButton:
                    commands.Add(CreatePointerButton(input.Button, input.IsPressed));
                    break;
                case RemoteInputEventKind.PointerWheel:
                    commands.Add(CreateMouse(
                        0,
                        0,
                        unchecked((uint)input.WheelDelta),
                        input.IsHorizontalWheel ? MouseHWheel : MouseWheel));
                    break;
                case RemoteInputEventKind.Key:
                    commands.Add(CreateKey(input.VirtualKey, input.IsPressed, input.IsExtendedKey));
                    break;
                default:
                    return false;
            }

            if (SendInput((uint)commands.Count, [.. commands], Marshal.SizeOf<NativeInput>()) != (uint)commands.Count)
            {
                var errorCode = Marshal.GetLastWin32Error();
                _enabled = false;
                // A partial SendInput result does not identify which item was accepted. Track a
                // possible new held state and issue explicit ups. If Windows rejects those too,
                // retain the state so a later safe release can retry instead of forgetting it.
                if (input.Kind == RemoteInputEventKind.Key && input.IsPressed)
                    _pressedKeys.Add(input.VirtualKey);
                else if (input.Kind == RemoteInputEventKind.PointerButton && input.IsPressed)
                    _pressedButtons.Add(input.Button);
                _log.LogWarning(
                    "Windows rejected authorized remote input with Win32 error {ErrorCode}; input was disabled",
                    errorCode);
                ReleaseAllCore();
                return false;
            }

            if (!_successLogged)
            {
                _successLogged = true;
                // This intentionally records no command kind, key, button, pointer coordinate,
                // window title, or user content. It is a one-time transport/injection health gate.
                _log.LogInformation("Authorized remote input reached the Windows injection boundary successfully");
            }

            if (input.Kind == RemoteInputEventKind.Key)
            {
                if (input.IsPressed) _pressedKeys.Add(input.VirtualKey);
                else _pressedKeys.Remove(input.VirtualKey);
            }
            else if (input.Kind == RemoteInputEventKind.PointerButton)
            {
                if (input.IsPressed) _pressedButtons.Add(input.Button);
                else _pressedButtons.Remove(input.Button);
            }

            return true;
        }
    }

    private bool ReleaseAllCore()
    {
        if (_pressedKeys.Count == 0 && _pressedButtons.Count == 0) return true;

        var releases = new List<NativeInput>(_pressedKeys.Count + _pressedButtons.Count);
        releases.AddRange(_pressedKeys.Select(key => CreateKey(key, isPressed: false, IsExtendedKey(key))));
        releases.AddRange(_pressedButtons.Select(button => CreatePointerButton(button, isPressed: false)));
        var sent = SendInput((uint)releases.Count, [.. releases], Marshal.SizeOf<NativeInput>());
        if (sent != (uint)releases.Count)
        {
            _log.LogWarning(
                "Windows rejected one or more remote-input release events with Win32 error {ErrorCode}; held state remains tracked for retry",
                Marshal.GetLastWin32Error());
            return false;
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
        return true;
    }

    private bool IsPressed(params ushort[] keys) => keys.Any(_pressedKeys.Contains);

    private static DisplayBounds? ResolveBounds(CaptureTargetInfo target)
    {
        if (target.Kind != CaptureTargetKind.Display) return null;
        var display = DisplayEnumerator.FindByDeviceName(target.Id);
        return display is null
            ? null
            : new DisplayBounds(display.X, display.Y, display.Width, display.Height);
    }

    private static NativeInput CreateAbsoluteMove(double normalizedX, double normalizedY, DisplayBounds target)
    {
        var virtualX = GetSystemMetrics(SmXVirtualScreen);
        var virtualY = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var virtualHeight = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        var pixelX = target.X + (normalizedX * Math.Max(0, target.Width - 1));
        var pixelY = target.Y + (normalizedY * Math.Max(0, target.Height - 1));
        var absoluteX = (int)Math.Round((pixelX - virtualX) * 65_535d / Math.Max(1, virtualWidth - 1));
        var absoluteY = (int)Math.Round((pixelY - virtualY) * 65_535d / Math.Max(1, virtualHeight - 1));
        return CreateMouse(
            Math.Clamp(absoluteX, 0, 65_535),
            Math.Clamp(absoluteY, 0, 65_535),
            0,
            MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    private static NativeInput CreatePointerButton(RemotePointerButton button, bool isPressed)
    {
        var (down, up, data) = button switch
        {
            RemotePointerButton.Left => (MouseLeftDown, MouseLeftUp, 0u),
            RemotePointerButton.Right => (MouseRightDown, MouseRightUp, 0u),
            RemotePointerButton.Middle => (MouseMiddleDown, MouseMiddleUp, 0u),
            RemotePointerButton.X1 => (MouseXDown, MouseXUp, XButton1),
            RemotePointerButton.X2 => (MouseXDown, MouseXUp, XButton2),
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
        return CreateMouse(0, 0, data, isPressed ? down : up);
    }

    private static NativeInput CreateMouse(int dx, int dy, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = data,
                Flags = flags,
            },
        },
    };

    private static NativeInput CreateKey(ushort virtualKey, bool isPressed, bool isExtended) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = (isPressed ? 0u : KeyUp) | (isExtended ? KeyExtended : 0u),
            },
        },
    };

    private static bool IsExtendedKey(ushort virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0x91 or 0xA3 or 0xA5;

    private sealed record DisplayBounds(int X, int Y, int Width, int Height);

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyDelete = 0x2E;
    private const ushort VirtualKeyLeftControl = 0xA2;
    private const ushort VirtualKeyRightControl = 0xA3;
    private const ushort VirtualKeyLeftMenu = 0xA4;
    private const ushort VirtualKeyRightMenu = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

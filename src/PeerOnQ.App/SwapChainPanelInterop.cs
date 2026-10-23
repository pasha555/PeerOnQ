using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace PeerOnQ.App;

/// <summary>
/// ISwapChainPanelNative (microsoft.ui.xaml.media.dxinterop.h): attaches a DXGI swap chain to a
/// WinUI SwapChainPanel. Called through the raw vtable so it needs no built-in COM interop.
/// Must run on the panel's UI thread.
/// </summary>
internal static class SwapChainPanelInterop
{
    private static readonly Guid SwapChainPanelNativeId = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static unsafe void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var inspectable = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in SwapChainPanelNativeId, out var native));
        try
        {
            // IUnknown occupies slots 0-2; SetSwapChain is the interface's only method.
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}

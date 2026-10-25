using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PeerOnQ.Media.Codecs;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace PeerOnQ.Platform.Windows.Media;

internal interface IMediaFoundationD3D11DecoderOutput : IDisposable
{
    void Attach(IMFTransform transform);
    byte[] Copy(IMFSample sample, int width, int height);
}

/// <summary>Optional hardware-device decoder surfaces copied synchronously to owned NV12.</summary>
internal sealed class MediaFoundationD3D11DecoderOutput : IMediaFoundationD3D11DecoderOutput
{
    private readonly ID3D11Device _device;
    private readonly IMFDXGIDeviceManager _manager;
    private int _supportedWidth, _supportedHeight;
    private bool _disposed;

    internal MediaFoundationD3D11DecoderOutput()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device).CheckError();
        _device = device ?? throw new InvalidOperationException("No hardware D3D11 device.");
        try
        {
            using var context = _device.ImmediateContext;
            using var multithread = context.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            dxgi.GetAdapter(out var adapter).CheckError();
            using (adapter)
            using (var adapter1 = adapter.QueryInterface<IDXGIAdapter1>())
                if (((int)adapter1.Description1.Flags & 2) != 0)
                    throw new NotSupportedException("A software adapter cannot supply hardware decoder output.");
            _manager = MFCreateDXGIDeviceManager();
            try { _manager.ResetDevice(_device); }
            catch { _manager.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
    }

    public void Attach(IMFTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var attributes = transform.Attributes;
        if (attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) == 0)
            throw new NotSupportedException("The installed decoder does not support D3D11 output.");
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_manager.NativePointer);
    }

    public byte[] Copy(IMFSample sample, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!VideoCodecLimits.ValidDimensions(width, height))
            throw new ArgumentException("Bounded NV12 dimensions are required.");
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        using var owner = texture.Device;
        var desc = texture.Description;
        if (owner.NativePointer != _device.NativePointer || desc.Format != Format.NV12 || desc.MipLevels != 1
            || (desc.BindFlags & BindFlags.Decoder) == 0 || dxgi.SubresourceIndex >= desc.ArraySize
            || desc.Width < width || desc.Width > width + 64 || (desc.Width & 1) != 0
            || desc.Height < height || desc.Height > height + 64 || (desc.Height & 1) != 0)
            throw new InvalidOperationException("The decoder returned an unsupported D3D11 surface.");
        if (_supportedWidth != width || _supportedHeight != height)
        {
            using var video = _device.QueryInterface<ID3D11VideoDevice>();
            var descriptor = new VideoDecoderDescription
            {
                Guid = new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5"), // H264 VLD, no film grain.
                SampleWidth = (uint)width, SampleHeight = (uint)height, OutputFormat = Format.NV12,
            };
            video.GetVideoDecoderConfigCount(ref descriptor, out var configurations).CheckError();
            if (configurations == 0) throw new NotSupportedException("No hardware H.264 configuration for this size.");
            _supportedWidth = width; _supportedHeight = height;
        }
        using var twoD = buffer.QueryInterface<IMF2DBuffer2>();
        twoD.Lock2DSize(Buffer2DLockFlags.Read, out var scan0, out var pitch, out var start, out var length);
        byte[]? owned = null;
        try
        {
            var offset = checked(scan0.ToInt64() - start.ToInt64());
            var uvOffset = checked((long)pitch * desc.Height);
            var required = checked(offset + uvOffset + (height / 2L - 1) * pitch + width);
            if (pitch < desc.Width || (pitch & 1) != 0 || pitch > VideoCodecLimits.MaximumDimension + 64
                || offset < 0 || required > length
                || length > (long)VideoCodecLimits.MaximumPixels * 3 / 2 + 1024 * 1024)
                throw new InvalidOperationException("The mapped NV12 layout exceeds its bounded buffer.");
            owned = GC.AllocateUninitializedArray<byte>(checked(width * height * 3 / 2));
            for (var row = 0; row < height; row++)
                Marshal.Copy(scan0 + checked(row * pitch), owned, row * width, width);
            for (var row = 0; row < height / 2; row++)
                Marshal.Copy(scan0 + checked((int)uvOffset + row * pitch), owned, width * height + row * width, width);
            return owned;
        }
        catch { if (owned is not null) CryptographicOperations.ZeroMemory(owned); throw; }
        finally
        {
            try { twoD.Unlock2D(); }
            catch { if (owned is not null) CryptographicOperations.ZeroMemory(owned); throw; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _manager.Dispose(); }
        finally { _device.Dispose(); }
    }
}

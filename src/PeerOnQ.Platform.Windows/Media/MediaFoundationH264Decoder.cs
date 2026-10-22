using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;
using static PeerOnQ.Platform.Windows.Media.MediaFoundationVideoUtilities;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>Windows H.264 decoder with optional hardware-device D3D11 output and CPU fallback.</summary>
public sealed class MediaFoundationH264Decoder : IDeferredH264VideoDecoder
{
    private readonly Lock _gate = new();
    private readonly Func<IMediaFoundationD3D11DecoderOutput?> _outputFactory;
    private IMediaFoundationD3D11DecoderOutput? _d3dOutput;
    private bool _d3dDisabled, _awaitingCpuRecovery;
    private int _hardwareOutputReceived;
    private IMFActivate? _activation;
    private IMFTransform? _transform;
    private bool _started, _disposed;
    private int _width, _height, _stride, _bufferHeight;
    private long _timestamp;
    private string _decoderName = "Media Foundation H.264";
    private static readonly Guid MaximumCodedWidth = new("5ae557b8-77af-41f5-9fa6-4db2fe1d4bca");
    private static readonly Guid MaximumCodedHeight = new("7262a16a-d2dc-4e75-9ba8-65c0c6d32b13");
    public string Name { get; private set; } = "Media Foundation H.264";
    public bool HardwareAccelerated => Volatile.Read(ref _hardwareOutputReceived) != 0;
    internal double LastNativeDecodeMilliseconds { get; private set; }
    internal double LastColorConversionMilliseconds { get; private set; }

    public MediaFoundationH264Decoder() : this(() => new MediaFoundationD3D11DecoderOutput()) { }

    internal MediaFoundationH264Decoder(Func<IMediaFoundationD3D11DecoderOutput?> outputFactory)
    {
        _outputFactory = outputFactory;
        try
        {
            MFStartup().CheckError();
            _started = true;
            OpenTransform();
        }
        catch { Dispose(); throw; }
    }

    public static IH264VideoDecoder? TryCreate()
    {
        try { return new MediaFoundationH264Decoder(); }
        catch (Exception) { return null; } // Windows N / missing decoder: offer VP8 only.
    }

    private void OpenTransform()
    {
        using var activations = Enumerate(TransformCategoryGuids.VideoDecoder, 0x41,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Nv12 });
        foreach (var activation in activations)
        {
            try
            {
                _transform = activation.ActivateObject<IMFTransform>();
                _activation = activation.QueryInterface<IMFActivate>();
                _decoderName = Name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                using var attributes = _transform.Attributes;
                attributes.Set(LowLatency, 1u);
                attributes.Set(MaximumCodedWidth, 4096u);
                attributes.Set(MaximumCodedHeight, 2304u);
                attributes.Set(new Guid("9561c3e8-ea9e-4435-9b1e-a93e691894d8"), 1u);
                _ = SetCodecValue(_transform, LowLatency, 1); // Microsoft decoder uses VT_UI4.
                if (!_d3dDisabled)
                {
                    try
                    {
                        _d3dOutput ??= _outputFactory();
                        if (_d3dOutput is null) _d3dDisabled = true;
                        else _d3dOutput.Attach(_transform);
                    }
                    catch (Exception)
                    {
                        // A partly attached manager must not contaminate the CPU transform.
                        ResetTransform();
                        DisableD3DOutput();
                        OpenTransform();
                    }
                }
                return;
            }
            catch (Exception) { Close(_activation, _transform); _activation = null; _transform = null; }
        }
        throw new InvalidOperationException("No installed H.264 decoder supports NV12 output.");
    }

    public RemoteVideoFrame[] Decode(int width, int height, byte[] frame, bool reset)
    {
        var images = DecodeNv12(width, height, frame, reset);
        try
        {
            return images.Select(image => new RemoteVideoFrame(image.Width, image.Height,
                ConvertNv12ToBgr(image), RemotePixelFormat.Bgr24, TimeSpan.Zero)
                { BufferOwnershipCanTransfer = true }).ToArray();
        }
        finally
        {
            foreach (var image in images) CryptographicOperations.ZeroMemory(image.Pixels);
        }
    }

    public byte[] ConvertNv12ToBgr(DecodedNv12Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.HasValidLayout)
            throw new ArgumentException("A complete bounded NV12 image is required.", nameof(frame));
        var conversionBegan = Stopwatch.GetTimestamp();
        var bgr = Nv12ToBgr(frame.Width, frame.Height, frame.Stride, frame.BufferHeight, frame.Pixels);
        LastColorConversionMilliseconds = Stopwatch.GetElapsedTime(conversionBegan).TotalMilliseconds;
        return bgr;
    }

    public DecodedNv12Frame[] DecodeNv12(int width, int height, byte[] frame, bool reset)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!CompatibleH264Dimensions(width, height)
            || frame.Length == 0 || frame.Length > VideoCodecLimits.MaximumPayloadBytes)
            throw new ArgumentException("A bounded H.264 access unit is required.", nameof(frame));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_awaitingCpuRecovery && !IsRecoveryAccessUnit(frame))
                throw new InvalidOperationException("The CPU decoder is awaiting an H.264 recovery keyframe.");
            try
            {
                var images = DecodeNv12Core(width, height, frame, reset);
                // A consumed recovery IDR may legitimately precede delayed output.
                _awaitingCpuRecovery = false;
                return images;
            }
            catch (Exception error) when (_d3dOutput is not null)
            {
                ResetTransform();
                DisableD3DOutput();
                _width = _height = 0;
                _awaitingCpuRecovery = true;
                OpenTransform();
                // The authenticated receive path requests a throttled PLI on decode errors.
                throw new InvalidOperationException("D3D11 output failed; CPU recovery requires a keyframe.", error);
            }
        }
    }

    private DecodedNv12Frame[] DecodeNv12Core(int width, int height, byte[] frame, bool reset)
    {
        var nativeBegan = Stopwatch.GetTimestamp();
        LastNativeDecodeMilliseconds = LastColorConversionMilliseconds = 0;
        if (_width == 0 || reset || _width != width || _height != height)
        {
            if (_width != 0)
            {
                ResetTransform();
                OpenTransform();
            }
            _width = width; _height = height; _timestamp = 0;
            using (var attributes = _transform!.Attributes)
            {
                // Bound native SPS-driven allocation too, not only the outer envelope.
                attributes.Set(MaximumCodedWidth, (uint)((width + 15) & ~15));
                attributes.Set(MaximumCodedHeight, (uint)((height + 15) & ~15));
            }
            using var input = VideoType(H264, width, height, 30);
            _transform!.SetInputType(0, input, 0);
            SelectOutputType();
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
        using var sample = Sample(frame, _timestamp, 333_333, _transform!.GetInputStreamInfo(0).Alignment,
            terminateAccessUnit: true);
        _timestamp += 333_333;
        _transform!.ProcessInput(0, sample, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bytes = ReadOutput(_transform, VideoCodecLimits.MaximumPixels * 3 / 2 + 1024 * 1024, out var changed,
                out _, out _, captureTiming: false,
                readSample: _d3dOutput is null ? null : sample => _d3dOutput.Copy(sample, width, height));
            if (changed) { SelectOutputType(); continue; }
            LastNativeDecodeMilliseconds = Stopwatch.GetElapsedTime(nativeBegan).TotalMilliseconds;
            if (bytes is null) return [];
            var image = new DecodedNv12Frame(width, height,
                _d3dOutput is null ? _stride : width, _d3dOutput is null ? _bufferHeight : height, bytes);
            if (!image.HasValidLayout)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidOperationException("The H.264 decoder returned an invalid NV12 layout.");
            }
            if (_d3dOutput is not null)
            {
                if (!HardwareAccelerated) Name = $"{_decoderName} (D3D11 output)";
                Volatile.Write(ref _hardwareOutputReceived, 1);
            }
            return [image];
        }
        throw new InvalidOperationException("The H.264 decoder repeatedly changed its output format.");
    }

    private static bool IsRecoveryAccessUnit(ReadOnlySpan<byte> bytes)
    {
        var sps = false; var pps = false; var idr = false;
        var count = 0;
        for (var index = 0; index < bytes.Length;)
        {
            var prefix = PrefixLength(bytes, index);
            if (prefix == 0 || ++count > 4096) return false;
            var start = index + prefix;
            var end = start;
            while (end < bytes.Length && PrefixLength(bytes, end) == 0) end++;
            if (end - start < 2) return false;
            var header = bytes[start];
            if ((header & 0x80) != 0 || (header & 31) is 0 or >= 24) return false;
            sps |= (header & 31) == 7; pps |= (header & 31) == 8; idr |= (header & 31) == 5;
            index = end;
        }
        return sps && pps && idr;
    }

    private static int PrefixLength(ReadOnlySpan<byte> bytes, int index) =>
        index + 3 <= bytes.Length && bytes[index] == 0 && bytes[index + 1] == 0
            ? bytes[index + 2] == 1 ? 3
                : index + 4 <= bytes.Length && bytes[index + 2] == 0 && bytes[index + 3] == 1 ? 4 : 0
            : 0;

    private void ResetTransform()
    {
        Volatile.Write(ref _hardwareOutputReceived, 0);
        Name = _decoderName;
        try
        {
            if (_d3dOutput is not null && _transform is not null)
            {
                try { _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch (Exception) { }
                try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch (Exception) { }
                try { _transform.ProcessMessage(TMessageType.MessageSetD3DManager, 0); } catch (Exception) { }
            }
        }
        finally
        {
            try { Close(_activation, _transform); }
            catch (Exception) when (_d3dOutput is not null) { } // Optional device teardown cannot block CPU recovery.
            finally { _activation = null; _transform = null; }
        }
    }

    private void DisableD3DOutput()
    {
        _d3dDisabled = true;
        Volatile.Write(ref _hardwareOutputReceived, 0);
        Name = _decoderName;
        var output = _d3dOutput;
        _d3dOutput = null;
        try { output?.Dispose(); }
        catch (Exception) { } // CPU decoding remains available if optional COM teardown fails.
    }

    private void SelectOutputType()
    {
        for (var index = 0; index < 64; index++)
        {
            using var output = _transform!.GetOutputAvailableType(0, index);
            if (output.GetGUID(MediaTypeAttributeKeys.Subtype) != Nv12) continue;
            var size = output.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            UnpackSize(size, out var nativeWidth, out var nativeHeight);
            if (nativeWidth < _width || nativeHeight < _height
                || nativeWidth > _width + 64 || nativeHeight > _height + 64
                || (long)nativeWidth * nativeHeight > VideoCodecLimits.MaximumPixels + 1024 * 1024)
                throw new InvalidOperationException("The H.264 output disagrees with the authenticated dimensions.");
            _stride = (int)MFGetAttributeUInt32(output, MediaTypeAttributeKeys.DefaultStride, nativeWidth);
            _bufferHeight = checked((int)nativeHeight);
            if (_stride < nativeWidth || _stride > VideoCodecLimits.MaximumDimension + 64)
                throw new InvalidOperationException("The H.264 decoder returned an invalid NV12 stride.");
            _transform.SetOutputType(0, output, 0);
            using var attributes = _transform.Attributes;
            attributes.Set(LowLatency, 1u);
            return;
        }
        throw new InvalidOperationException("The H.264 decoder did not offer bounded NV12 output.");
    }

    private static readonly Vector128<byte> BgrShuffle = Vector128.Create(
        (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 128, 128, 128, 128);
    private static readonly Vector128<byte> UShuffle = Vector128.Create(
        (byte)0, 0, 2, 2, 4, 4, 6, 6, 128, 128, 128, 128, 128, 128, 128, 128);
    private static readonly Vector128<byte> VShuffle = Vector128.Create(
        (byte)1, 1, 3, 3, 5, 5, 7, 7, 128, 128, 128, 128, 128, 128, 128, 128);
    private static bool VectorizedConversionSupported =>
        Avx2.IsSupported && Avx.IsSupported && Sse41.IsSupported && Ssse3.IsSupported && Sse2.IsSupported;

    internal static unsafe byte[] Nv12ToBgr(int width, int height, int stride, int bufferHeight, byte[] source)
    {
        if (!VideoCodecLimits.ValidDimensions(width, height) || stride < width
            || stride > VideoCodecLimits.MaximumDimension + 64 || bufferHeight < height
            || bufferHeight > height + 64 || source.Length < (long)stride * bufferHeight * 3 / 2)
            throw new ArgumentException("A complete bounded NV12 image is required.", nameof(source));
        // Each visible packed BGR byte is written by exactly one row below.
        var bgr = GC.AllocateUninitializedArray<byte>(checked(width * height * 3));
        if (VectorizedConversionSupported)
        {
            fixed (byte* sourcePointer = source)
            fixed (byte* targetPointer = bgr)
            {
                var sourceAddress = (nint)sourcePointer;
                var targetAddress = (nint)targetPointer;
                void ConvertVectorRow(int row) => ConvertNv12Row(
                    (byte*)sourceAddress + row * stride,
                    (byte*)sourceAddress + stride * bufferHeight + row / 2 * stride,
                    (byte*)targetAddress + row * width * 3, width);
                if ((long)width * height >= 1920 * 1080)
                    Parallel.For(0, height, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    }, ConvertVectorRow);
                else for (var row = 0; row < height; row++) ConvertVectorRow(row);
            }
            return bgr;
        }
        // ARM64 and hosts without the complete instruction set retain the original scalar path.
        void ConvertRow(int row)
        {
            var luma = row * stride;
            var chroma = stride * bufferHeight + row / 2 * stride;
            var target = row * width * 3;
            for (var x = 0; x < width; x++)
            {
                var y = Math.Max(0, source[luma + x] - 16);
                var u = source[chroma + (x & ~1)] - 128;
                var v = source[chroma + (x & ~1) + 1] - 128;
                bgr[target++] = (byte)Math.Clamp((298 * y + 516 * u + 128) >> 8, 0, 255);
                bgr[target++] = (byte)Math.Clamp((298 * y - 100 * u - 208 * v + 128) >> 8, 0, 255);
                bgr[target++] = (byte)Math.Clamp((298 * y + 409 * v + 128) >> 8, 0, 255);
            }
        }
        if ((long)width * height >= 1920 * 1080)
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, ConvertRow);
        else for (var row = 0; row < height; row++) ConvertRow(row);
        return bgr;
    }

    private static unsafe void ConvertNv12Row(byte* y, byte* uv, byte* target, int width)
    {
        var x = 0;
        for (; x <= width - 8; x += 8)
        {
            var luma = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(y + x)).AsByte();
            var chroma = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(uv + x)).AsByte();
            var yy = Avx2.Max(Avx2.Subtract(Avx2.ConvertToVector256Int32(luma), Vector256.Create(16)), Vector256<int>.Zero);
            var u = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(chroma, UShuffle)), Vector256.Create(128));
            var v = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(chroma, VShuffle)), Vector256.Create(128));
            var scaledY = Avx2.MultiplyLow(yy, Vector256.Create(298));
            var b = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(scaledY,
                Avx2.MultiplyLow(u, Vector256.Create(516))), Vector256.Create(128)), 8);
            var g = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Subtract(Avx2.Subtract(scaledY,
                Avx2.MultiplyLow(u, Vector256.Create(100))), Avx2.MultiplyLow(v, Vector256.Create(208))), Vector256.Create(128)), 8);
            var r = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.Add(scaledY,
                Avx2.MultiplyLow(v, Vector256.Create(409))), Vector256.Create(128)), 8);
            WriteFourBgrPixels(target + x * 3, b.GetLower(), g.GetLower(), r.GetLower());
            WriteFourBgrPixels(target + x * 3 + 12, b.GetUpper(), g.GetUpper(), r.GetUpper());
        }
        for (; x < width; x++)
        {
            var yy = Math.Max(0, y[x] - 16);
            var u = uv[x & ~1] - 128;
            var v = uv[(x & ~1) + 1] - 128;
            target[x * 3] = (byte)Math.Clamp((298 * yy + 516 * u + 128) >> 8, 0, 255);
            target[x * 3 + 1] = (byte)Math.Clamp((298 * yy - 100 * u - 208 * v + 128) >> 8, 0, 255);
            target[x * 3 + 2] = (byte)Math.Clamp((298 * yy + 409 * v + 128) >> 8, 0, 255);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteFourBgrPixels(byte* target, Vector128<int> b, Vector128<int> g, Vector128<int> r)
    {
        var maximum = Vector128.Create(255);
        b = Sse41.Min(Sse41.Max(b, Vector128<int>.Zero), maximum);
        g = Sse41.Min(Sse41.Max(g, Vector128<int>.Zero), maximum);
        r = Sse41.Min(Sse41.Max(r, Vector128<int>.Zero), maximum);
        var bgra = Sse2.Or(Sse2.Or(b, Sse2.ShiftLeftLogical(g, 8)), Sse2.ShiftLeftLogical(r, 16));
        var packed = Ssse3.Shuffle(bgra.AsByte(), BgrShuffle);
        // Two bounded stores write twelve bytes, never padding or the next row.
        Unsafe.WriteUnaligned(target, packed.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(target + 8, packed.AsUInt32().GetElement(2));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { ResetTransform(); }
            finally
            {
                try { DisableD3DOutput(); }
                finally { if (_started) { _ = MFShutdown(); _started = false; } }
            }
        }
    }
}

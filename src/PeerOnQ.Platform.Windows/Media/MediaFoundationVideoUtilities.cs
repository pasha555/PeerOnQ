using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Collections;
using PeerOnQ.Media.Codecs;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace PeerOnQ.Platform.Windows.Media;

internal static class MediaFoundationVideoUtilities
{
    internal const int NeedMoreInput = unchecked((int)0xc00d6d72);
    internal const int StreamChange = unchecked((int)0xc00d6d61);
    internal const int NoEvents = unchecked((int)0xc00d3e80);
    internal static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    internal static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    // Interoperate with the documented Windows inbox H.264 decoder range. Larger or portrait
    // sources remain valid desktop sources and use VP8 instead of an undecodable H.264 stream.
    internal static bool CompatibleH264Dimensions(int width, int height) =>
        VideoCodecLimits.ValidDimensions(width, height) && width >= 48 && height >= 48
        && width <= 4096 && height <= 2304;

    internal static ActivationLease Enumerate(Guid category, uint flags, RegisterTypeInfo input, RegisterTypeInfo output)
    {
        // Vortice 3.8.3's collection helper does not free the MFTEnumEx array. Retain the original
        // base pointer here and balance both the array allocation and every activation reference.
        MFTEnumEx(category, flags, input, output, out var pointer, out var count);
        var owned = new List<IMFActivate>();
        try
        {
            if (count > 256) throw new InvalidOperationException("Too many registered video transforms.");
            for (var index = 0; index < count; index++)
                owned.Add(new IMFActivate(Marshal.ReadIntPtr(pointer, checked(index * nint.Size))));
            return new ActivationLease(owned);
        }
        catch
        {
            foreach (var activation in owned) activation.Dispose();
            for (var index = owned.Count; index < count && pointer != 0; index++)
                Marshal.Release(Marshal.ReadIntPtr(pointer, checked(index * nint.Size)));
            throw;
        }
        finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }

    internal sealed class ActivationLease(List<IMFActivate> activations) : IEnumerable<IMFActivate>, IDisposable
    {
        public IEnumerator<IMFActivate> GetEnumerator() => activations.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { foreach (var activation in activations) activation.Dispose(); }
    }

    internal static IMFMediaType VideoType(Guid subtype, int width, int height, int frameRate)
    {
        var type = MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            type.Set(MediaTypeAttributeKeys.FrameSize, PackSize((uint)width, (uint)height));
            type.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(frameRate, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    internal static IMFSample Sample(byte[] bytes, long timestamp, long duration, int alignment = 0,
        bool terminateAccessUnit = false)
    {
        var sample = MFCreateSample();
        try
        {
            // An AUD closes the preceding complete Annex B picture without introducing another
            // picture. This lets the Windows parser present a lone change-driven desktop frame.
            byte[] delimiter = [0, 0, 0, 1, 9, 0xf0];
            var size = checked(bytes.Length + (terminateAccessUnit ? delimiter.Length : 0));
            using var buffer = MemoryBuffer(size, alignment);
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                if (terminateAccessUnit) Marshal.Copy(delimiter, 0, pointer + bytes.Length, delimiter.Length);
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = size;
            sample.AddBuffer(buffer);
            sample.SampleTime = timestamp;
            sample.SampleDuration = duration;
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }

    internal static byte[]? ReadOutput(IMFTransform transform, int maximumBytes, out bool streamChange)
        => ReadOutput(transform, maximumBytes, out streamChange, out _, out _, captureTiming: false);

    internal static byte[]? ReadOutput(IMFTransform transform, int maximumBytes, out bool streamChange,
        out long? sampleTime, out long? sampleDuration, bool captureTiming = true,
        Func<IMFSample, byte[]>? readSample = null)
    {
        streamChange = false;
        sampleTime = sampleDuration = null;
        var info = transform.GetOutputStreamInfo(0);
        IMFSample? supplied = null;
        var output = new OutputDataBuffer { StreamID = 0 };
        try
        {
            if ((info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
            {
                if (info.Size <= 0 || info.Size > maximumBytes)
                    throw new InvalidOperationException("The codec requested an invalid output buffer size.");
                supplied = MFCreateSample();
                using var buffer = MemoryBuffer(info.Size, info.Alignment);
                supplied.AddBuffer(buffer);
                output.Sample = supplied;
            }
            var result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result.Code == NeedMoreInput) return null;
            if (result.Code == StreamChange) { streamChange = true; return null; }
            result.CheckError();
            if (output.Sample is null) return null;
            if (captureTiming)
            {
                try { sampleTime = output.Sample.SampleTime; } catch (Exception) { }
                try { sampleDuration = output.Sample.SampleDuration; } catch (Exception) { }
            }
            if (readSample is not null) return readSample(output.Sample);
            using var contiguous = output.Sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var pointer, out _, out var length);
            try
            {
                if (length <= 0 || length > maximumBytes)
                    throw new InvalidOperationException("The codec returned an invalid output size.");
                // Marshal.Copy fills the entire bounded buffer before it is returned.
                var bytes = GC.AllocateUninitializedArray<byte>(length);
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            }
            finally { contiguous.Unlock(); }
        }
        finally
        {
            output.Events?.Dispose();
            if (output.Sample is not null && !ReferenceEquals(output.Sample, supplied))
            {
                // SharpGen's in/out marshaller creates a new wrapper for the same borrowed
                // sample pointer; it does not AddRef. Never release the one reference twice.
                if (supplied is not null && output.Sample.NativePointer == supplied.NativePointer)
                    output.Sample.NativePointer = 0;
                output.Sample.Dispose();
            }
            supplied?.Dispose();
        }
    }

    private static IMFMediaBuffer MemoryBuffer(int size, int alignment)
    {
        if (alignment < 0 || alignment > 4095 || (alignment & (alignment + 1)) != 0)
            throw new InvalidOperationException("The codec requested an invalid buffer alignment.");
        return alignment == 0 ? MFCreateMemoryBuffer(size) : MFCreateAlignedMemoryBuffer(size, alignment);
    }

    // ICodecAPI is not exposed by Vortice.MediaFoundation. Use its documented SetValue slot
    // and a blittable VARIANT containing only VT_BOOL / VT_UI4 (no allocations or secret data).
    internal static unsafe bool SetCodecValue(IMFTransform transform, Guid property, uint value, bool boolean = false)
    {
        var iid = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        if (Marshal.QueryInterface(transform.NativePointer, in iid, out var codec) < 0) return false;
        try
        {
            var variant = new CodecVariant { Type = boolean ? (ushort)11 : (ushort)19, Value = value };
            var method = (delegate* unmanaged[Stdcall]<nint, Guid*, CodecVariant*, int>)(*(nint**)codec)[9];
            return method(codec, &property, &variant) >= 0;
        }
        finally { Marshal.Release(codec); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CodecVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Value;
    }

    internal static void Close(IMFActivate? activation, IMFTransform? transform)
    {
        try
        {
            if (transform is not null)
            {
                try { transform.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch (Exception) { }
                try { transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch (Exception) { }
            }
            try { activation?.ShutdownObject(); } catch (Exception) { }
        }
        finally { transform?.Dispose(); activation?.Dispose(); }
    }

    internal static byte[] I420ToNv12(int width, int height, byte[] source)
    {
        ValidateI420(width, height, source);
        var output = GC.AllocateUninitializedArray<byte>(source.Length);
        I420ToNv12(width, height, source, output);
        return output;
    }

    internal static void ValidateI420(int width, int height, byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!VideoCodecLimits.ValidDimensions(width, height)
            || source.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("A bounded, complete I420 image is required.", nameof(source));
    }

    internal static void I420ToNv12(int width, int height, byte[] source, Span<byte> output)
    {
        ValidateI420(width, height, source);
        if (output.Length != source.Length || source.AsSpan().Overlaps(output))
            throw new ArgumentException("An independent complete NV12 destination is required.", nameof(output));
        var luma = width * height;
        var quarter = luma / 4;
        source.AsSpan(0, luma).CopyTo(output);
        var u = source.AsSpan(luma, quarter);
        var v = source.AsSpan(luma + quarter, quarter);
        var uv = output.Slice(luma, quarter * 2);
        var index = 0;
        if (Vector128.IsHardwareAccelerated && quarter >= Vector128<byte>.Count)
        {
            // Widening U and V to 16-bit lanes and OR-ing V into the high byte produces the
            // little-endian UVUV... byte order exactly, sixteen chroma pairs per iteration. A 4K
            // frame previously spent most of its ~18 ms format stage in the scalar loop below.
            ref var uStart = ref MemoryMarshal.GetReference(u);
            ref var vStart = ref MemoryMarshal.GetReference(v);
            ref var uvStart = ref MemoryMarshal.GetReference(uv);
            for (; index <= quarter - Vector128<byte>.Count; index += Vector128<byte>.Count)
            {
                var uBytes = Vector128.LoadUnsafe(ref uStart, (nuint)index);
                var vBytes = Vector128.LoadUnsafe(ref vStart, (nuint)index);
                var low = Vector128.WidenLower(uBytes) | (Vector128.WidenLower(vBytes) << 8);
                var high = Vector128.WidenUpper(uBytes) | (Vector128.WidenUpper(vBytes) << 8);
                low.AsByte().StoreUnsafe(ref uvStart, (nuint)(index * 2));
                high.AsByte().StoreUnsafe(ref uvStart, (nuint)(index * 2 + Vector128<byte>.Count));
            }
        }
        for (; index < quarter; index++)
        {
            uv[2 * index] = u[index];
            uv[2 * index + 1] = v[index];
        }
    }
}

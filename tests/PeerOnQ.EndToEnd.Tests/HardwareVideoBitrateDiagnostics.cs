using System.Reflection;
using System.Runtime.InteropServices;
using PeerOnQ.Platform.Windows.Media;
using Vortice.MediaFoundation;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>Reads codec configuration and Annex B type/size metadata without changing an access unit.</summary>
internal static partial class HardwareVideoBitrateDiagnostics
{
    private static readonly FieldInfo TransformField = typeof(MediaFoundationH264Encoder)
        .GetField("_transform", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The encoder transform diagnostic field changed.");

    internal static string Describe(MediaFoundationH264Encoder encoder, ReadOnlySpan<byte> encoded)
    {
        var transform = TransformField.GetValue(encoder) as IMFTransform;
        var control = transform is null ? "unavailable" : ReadCodecValue(transform, new("1c0608e9-370c-4710-8a58-cb6181c42423"));
        var mean = transform is null ? "unavailable" : ReadCodecValue(transform, MediaFoundationVideoUtilities.MeanBitRate);
        var average = "unavailable";
        if (transform is not null)
        {
            using var output = transform.GetOutputCurrentType(0);
            average = output.GetUInt32(MediaTypeAttributeKeys.AvgBitrate).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var nals = new Dictionary<int, (int Count, int Bytes)>();
        var fillerShapes = new List<string>();
        var previous = -1;
        var previousType = -1;
        var previousPrefix = 0;
        var firstPrefix = -1;
        for (var index = 0; index + 3 < encoded.Length; index++)
        {
            if (encoded[index] != 0 || encoded[index + 1] != 0) continue;
            var prefix = encoded[index + 2] == 1 ? 3
                : index + 4 < encoded.Length && encoded[index + 2] == 0 && encoded[index + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            if (previous >= 0)
            {
                Add(previousType, index - previous);
                if (previousType == 12) fillerShapes.Add(DescribeFiller(encoded, previous, previousPrefix, index));
            }
            previous = index;
            if (firstPrefix < 0) firstPrefix = index;
            previousType = encoded[index + prefix] & 31;
            previousPrefix = prefix;
            index += prefix - 1;
        }
        if (previous >= 0)
        {
            Add(previousType, encoded.Length - previous);
            if (previousType == 12) fillerShapes.Add(DescribeFiller(encoded, previous, previousPrefix, encoded.Length));
        }
        return $"readbackRateControlMode={control}; readbackMeanBitsPerSecond={mean}; outputAvgBitsPerSecond={average}; "
            + "NALtype=count/annexBBytes:" + string.Join(',', nals.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value.Count}/{pair.Value.Bytes}"))
            + $"; firstPrefixOffset={firstPrefix}; fillerShapes=" + string.Join('|', fillerShapes);

        void Add(int type, int length)
        {
            nals.TryGetValue(type, out var existing);
            nals[type] = (existing.Count + 1, existing.Bytes + length);
        }
    }

    private static string DescribeFiller(ReadOnlySpan<byte> encoded, int start, int prefix, int end)
    {
        var header = encoded[start + prefix];
        var body = start + prefix + 1;
        var zeros = 0;
        while (end - zeros > body && encoded[end - zeros - 1] == 0) zeros++;
        var ff = 0;
        var stop = 0;
        var other = 0;
        for (var index = body; index < end - zeros; index++)
        {
            if (encoded[index] == 0xff) ff++;
            else if (encoded[index] == 0x80) stop++;
            else other++;
        }
        var marker = end - zeros <= body ? "empty"
            : encoded[end - zeros - 1] == 0x80 ? "rbsp-stop80"
            : encoded[end - zeros - 1] == 0xff ? "ff" : "other";
        return $"prefix={prefix},F={header >> 7},NRI={(header >> 5) & 3},bodyBytes={end - body},"
            + $"FFbytes={ff},stop80Bytes={stop},otherBytes={other},trailingZeroBytes={zeros},lastNonzero={marker}";
    }

    private static unsafe string ReadCodecValue(IMFTransform transform, Guid property)
    {
        var iid = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        if (Marshal.QueryInterface(transform.NativePointer, in iid, out var codec) < 0) return "unavailable(no-ICodecAPI)";
        try
        {
            var variant = new CodecVariant();
            try
            {
                // ICodecAPI::GetValue, documented slot immediately before SetValue (slot9).
                var method = (delegate* unmanaged[Stdcall]<nint, Guid*, CodecVariant*, int>)(*(nint**)codec)[8];
                var result = method(codec, &property, &variant);
                if (result < 0) return $"unavailable(HRESULT=0x{result:x8})";
                return variant.Type switch
                {
                    19 => variant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), // VT_UI4
                    3 when variant.Value <= int.MaxValue => variant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), // VT_I4
                    _ => $"unavailable(VARTYPE={variant.Type})",
                };
            }
            finally { _ = VariantClear((nint)(&variant)); }
        }
        finally { Marshal.Release(codec); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CodecVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Value;
    }

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(nint variant);
}

using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>Removes only complete non-reference filler data from a known Annex B picture.</summary>
internal static class H264FillerDataFilter
{
    internal static byte[] Remove(byte[] encoded) => RemoveCore(encoded, allowNativeEofPadding: false);

    /// <summary>
    /// AMD's MFT can end its complete owned sample with F=0/NRI=0 filler containing only FF,
    /// omitting the standard RBSP stop bit. Normalize only this exact nonempty EOF padding.
    /// This is a native-output compatibility exception, never a remote-input validation rule.
    /// </summary>
    internal static byte[] RemoveFromNativeSample(byte[] encoded) => RemoveCore(encoded, allowNativeEofPadding: true);

    private static byte[] RemoveCore(byte[] encoded, bool allowNativeEofPadding)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length > VideoCodecLimits.MaximumPayloadBytes || PrefixLength(encoded, 0) == 0)
            return encoded;

        var removed = 0;
        var hasPicture = false;
        for (var start = 0; start < encoded.Length;)
        {
            if (!TryRead(encoded, start, out var payload, out var end, out var type)) return encoded;
            hasPicture |= type is >= 1 and <= 5;
            if (type == 12)
            {
                // filler_data_rbsp is zero or more FF bytes followed by rbsp_trailing_bits 80.
                // F and NRI must both be zero; ambiguous filler leaves the entire input intact.
                if (encoded[payload] != 12) return encoded;
                var standard = encoded[end - 1] == 0x80;
                if (!standard && (!allowNativeEofPadding || end != encoded.Length)) return encoded;
                var fillerEnd = standard ? end - 1 : end;
                for (var index = payload + 1; index < fillerEnd; index++)
                    if (encoded[index] != 0xff) return encoded;
                removed += end - start;
            }
            start = end;
        }
        if (!hasPicture || removed == 0) return encoded;

        var filtered = GC.AllocateUninitializedArray<byte>(encoded.Length - removed);
        var written = 0;
        for (var start = 0; start < encoded.Length;)
        {
            // The owned input is unchanged between validation and this exact byte-range copy.
            _ = TryRead(encoded, start, out _, out var end, out var type);
            if (type != 12)
            {
                encoded.AsSpan(start, end - start).CopyTo(filtered.AsSpan(written));
                written += end - start;
            }
            start = end;
        }
        return filtered;
    }

    private static bool TryRead(ReadOnlySpan<byte> encoded, int start, out int payload, out int end, out int type)
    {
        var prefix = PrefixLength(encoded, start);
        payload = start + prefix;
        end = encoded.Length;
        type = 0;
        if (prefix == 0 || payload >= encoded.Length) return false;
        for (var index = payload + 1; index < encoded.Length; index++)
        {
            if (PrefixLength(encoded, index) == 0) continue;
            end = index;
            break;
        }
        type = encoded[payload] & 31;
        // Restrict this optimization to known base H.264 units. Unknown/extension headers,
        // forbidden bits, empty RBSPs and incomplete prefixes preserve the original array.
        return end - payload >= 2 && (encoded[payload] & 0x80) == 0 && type is >= 1 and <= 12;
    }

    private static int PrefixLength(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 3 || bytes[offset] != 0 || bytes[offset + 1] != 0) return 0;
        if (bytes[offset + 2] == 1) return 3;
        return offset <= bytes.Length - 4 && bytes[offset + 2] == 0 && bytes[offset + 3] == 1 ? 4 : 0;
    }
}

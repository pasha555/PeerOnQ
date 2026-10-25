using PeerOnQ.Platform.Windows.Media;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class H264FillerDataFilterTests
{
    [Fact]
    public void Mixed_prefixes_remove_only_valid_filler_and_preserve_every_other_byte()
    {
        byte[] encoded = [
            0, 0, 0, 1, 0x67, 0x12, 0x80,
            0, 0, 1, 0x0c, 0xff, 0xff, 0x80,
            0, 0, 1, 0x68, 0x34, 0x80,
            0, 0, 0, 1, 0x06, 0, 0, 3, 1, 0x80,
            0, 0, 1, 0x09, 0xf0,
            0, 0, 0, 1, 0x65, 0x56, 0x80,
            0, 0, 0, 1, 0x0c, 0x80,
            0, 0, 1, 0x41, 0x78, 0x80,
        ];
        var original = encoded.ToArray();
        byte[] expected = [
            0, 0, 0, 1, 0x67, 0x12, 0x80,
            0, 0, 1, 0x68, 0x34, 0x80,
            0, 0, 0, 1, 0x06, 0, 0, 3, 1, 0x80,
            0, 0, 1, 0x09, 0xf0,
            0, 0, 0, 1, 0x65, 0x56, 0x80,
            0, 0, 1, 0x41, 0x78, 0x80,
        ];
        var filtered = H264FillerDataFilter.Remove(encoded);
        Assert.NotSame(encoded, filtered);
        Assert.Equal(expected, filtered);
        Assert.Equal(original, encoded);
        Assert.Same(filtered, H264FillerDataFilter.Remove(filtered));
    }

    [Fact]
    public void No_filler_returns_the_identical_array_including_emulation_prevention_bytes()
    {
        byte[] encoded = [0, 0, 0, 1, 0x65, 0x22, 0, 0, 3, 0, 0x80];
        Assert.Same(encoded, H264FillerDataFilter.Remove(encoded));
    }

    [Fact]
    public void Non_picture_configuration_and_filler_are_never_rewritten()
    {
        byte[] encoded = [0, 0, 1, 0x67, 0x80, 0, 0, 1, 0x68, 0x80, 0, 0, 1, 0x0c, 0xff, 0x80];
        Assert.Same(encoded, H264FillerDataFilter.Remove(encoded));
    }

    [Fact]
    public void Only_the_native_entry_point_normalizes_nonempty_all_FF_padding_at_sample_EOF()
    {
        byte[] picture = [0, 0, 0, 1, 0x65, 0x22, 0x80];
        byte[] encoded = [0, 0, 0, 1, 0x65, 0x22, 0x80, 0, 0, 0, 1, 0x0c, 0xff, 0xff];
        var original = encoded.ToArray();
        Assert.Same(encoded, H264FillerDataFilter.Remove(encoded));
        Assert.Equal(picture, H264FillerDataFilter.RemoveFromNativeSample(encoded));
        Assert.Equal(original, encoded);
    }

    [Theory]
    [MemberData(nameof(AmbiguousNativeInputs))]
    public void Native_exception_preserves_every_other_ambiguous_or_non_EOF_form(byte[] encoded)
    {
        var original = encoded.ToArray();
        Assert.Same(encoded, H264FillerDataFilter.RemoveFromNativeSample(encoded));
        Assert.Equal(original, encoded);
    }

    public static IEnumerable<object[]> AmbiguousNativeInputs()
    {
        yield return [new byte[] { 0, 0, 1, 0x0c, 0xff }]; // No picture.
        byte[] picture = [0, 0, 0, 1, 0x65, 0x22, 0x80];
        foreach (var tail in new byte[][]
        {
            [0, 0, 1, 0x0c], [0, 0, 1, 0x0c, 0x7f],
            [0, 0, 1, 0x0c, 0xff, 0], [0, 0, 1, 0x4c, 0xff],
            [0, 0, 1, 0x8c, 0xff], [0, 0, 1, 0x0c, 0xff, 0, 0, 1],
            [0, 0, 1, 0x0c, 0xff, 0, 0, 1, 0x41, 0x80], // Interior missing-stop filler.
            [0, 0, 1, 0x1f, 0x80, 0, 0, 1, 0x0c, 0xff], // Unknown unit.
        })
            yield return [picture.Concat(tail).ToArray()];
    }

    [Theory]
    [MemberData(nameof(AmbiguousInputs))]
    public void Malformed_unknown_or_ambiguous_access_units_remain_byte_identical(byte[] encoded)
    {
        var original = encoded.ToArray();
        Assert.Same(encoded, H264FillerDataFilter.Remove(encoded));
        Assert.Equal(original, encoded);
    }

    public static IEnumerable<object[]> AmbiguousInputs()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0, 0, 1 }];
        yield return [new byte[] { 0, 0, 0, 1, 0x65 }];
        // A valid picture and filler must still remain untouched if any later unit is uncertain.
        byte[] pictureAndFiller = [0, 0, 1, 0x65, 0x80, 0, 0, 1, 0x0c, 0xff, 0x80];
        foreach (var tail in new byte[][]
        {
            [0, 0, 1], [0, 0, 0, 1], [0, 0, 1, 0x0c],
            [0, 0, 1, 0x0c, 0xff], [0, 0, 1, 0x0c, 0x7f, 0x80],
            [0, 0, 1, 0x0c, 0xff, 0x80, 0],
            [0, 0, 1, 0x4c, 0xff, 0x80], [0, 0, 1, 0x8c, 0xff, 0x80],
            [0, 0, 1, 0x1f, 0x80], [0, 0, 1, 0x60, 0x80],
        })
            yield return [pictureAndFiller.Concat(tail).ToArray()];
        yield return [new byte[] { 0x55 }.Concat(pictureAndFiller).ToArray()];
    }
}

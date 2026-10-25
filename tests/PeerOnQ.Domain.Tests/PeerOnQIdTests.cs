using PeerOnQ.Domain.Identity;
using Xunit;

namespace PeerOnQ.Domain.Tests;

public class PeerOnQIdTests
{
    [Fact]
    public void NewId_matches_the_published_format()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = PeerOnQId.NewId();
            Assert.Matches(@"^LNK-\d{3}-\d{3}-\d{3}-\d{3}$", id.Value);
        }
    }

    [Fact]
    public void NewId_is_not_predictable_across_calls()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => PeerOnQId.NewId().Value).ToHashSet();

        // 12 random digits: collisions in 500 draws would indicate a broken generator.
        Assert.Equal(500, ids.Count);
    }

    [Theory]
    [InlineData("LNK-483-921-756-204")]
    [InlineData("lnk-483-921-756-204")]
    [InlineData("LNK 483 921 756 204")]
    [InlineData("  LNK-483-921-756-204  ")]
    [InlineData("483-921-756-204")]
    [InlineData("483921756204")]
    public void TryParse_normalises_accepted_input(string input)
    {
        Assert.True(PeerOnQId.TryParse(input, out var id));
        Assert.Equal("LNK-483-921-756-204", id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LNK-483-921-756")]
    [InlineData("LNK-483-921-756-2045")]
    [InlineData("ABC-483-921-756-204")]
    [InlineData("LNK-483-921-756-20A")]
    [InlineData("LNK-483-921-756-204-111")]
    [InlineData("drop table devices")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        Assert.False(PeerOnQId.TryParse(input, out _));
        Assert.False(PeerOnQId.IsValid(input));
    }

    [Fact]
    public void Parse_throws_on_invalid_input()
    {
        Assert.Throws<FormatException>(() => PeerOnQId.Parse("nope"));
    }

    [Fact]
    public void Masked_hides_the_middle_groups()
    {
        var id = PeerOnQId.Parse("LNK-483-921-756-204");

        Assert.Equal("LNK-483-***-***-204", id.Masked);
        Assert.Equal("483-***-***-204", id.MaskedDisplay);
        Assert.DoesNotContain("921", id.Masked);
        Assert.DoesNotContain("756", id.Masked);
    }

    [Theory]
    [InlineData("LNK-483-921-756-204", "483-***-***-204")]
    [InlineData("LNK-483-***-***-204", "483-***-***-204")]
    [InlineData("483-***-***-204", "483-***-***-204")]
    public void FormatMaskedDisplay_hides_the_protocol_prefix(string storedValue, string expected)
    {
        Assert.Equal(expected, PeerOnQId.FormatMaskedDisplay(storedValue));
    }

    [Fact]
    public void Digits_returns_the_bare_digits()
    {
        Assert.Equal("483921756204", PeerOnQId.Parse("LNK-483-921-756-204").Digits);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = PeerOnQId.Parse("LNK-483-921-756-204");
        var b = PeerOnQId.Parse("483 921 756 204");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}

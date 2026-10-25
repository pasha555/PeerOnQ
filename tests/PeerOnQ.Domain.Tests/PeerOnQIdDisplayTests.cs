using PeerOnQ.Domain.Identity;
using Xunit;

namespace PeerOnQ.Domain.Tests;

/// <summary>
/// The UI shows and copies the ID without the protocol prefix; the prefix stays on the wire
/// and in storage. Both forms must parse back to the same id.
/// </summary>
public class PeerOnQIdDisplayTests
{
    private static readonly PeerOnQId Id = PeerOnQId.Parse("LNK-049-769-287-726");

    [Fact]
    public void Display_drops_the_prefix_but_keeps_the_groups()
    {
        Assert.Equal("049-769-287-726", Id.Display);
        Assert.Equal("LNK-049-769-287-726", Id.Value);
    }

    [Fact]
    public void MaskedDisplay_hides_the_middle_groups()
    {
        Assert.Equal("049-***-***-726", Id.MaskedDisplay);
        Assert.DoesNotContain("769", Id.MaskedDisplay);
        Assert.DoesNotContain("287", Id.MaskedDisplay);
        Assert.DoesNotContain("LNK", Id.MaskedDisplay);
    }

    [Fact]
    public void What_the_user_copies_parses_back_to_the_same_id()
    {
        Assert.True(PeerOnQId.TryParse(Id.Display, out var parsed));
        Assert.Equal(Id, parsed);
    }

    [Fact]
    public void Both_forms_are_accepted_when_typing_a_remote_id()
    {
        Assert.True(PeerOnQId.TryParse("049-769-287-726", out var withoutPrefix));
        Assert.True(PeerOnQId.TryParse("LNK-049-769-287-726", out var withPrefix));

        Assert.Equal(withPrefix, withoutPrefix);
    }

    [Fact]
    public void Logs_keep_the_prefixed_masked_form()
    {
        // The log masking backstop matches LNK-xxx-***-***-xxx, so the log form must keep it.
        Assert.Equal("LNK-049-***-***-726", Id.Masked);
    }
}

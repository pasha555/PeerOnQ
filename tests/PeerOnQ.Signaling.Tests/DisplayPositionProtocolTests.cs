using System.Text;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class DisplayPositionProtocolTests
{
    [Fact]
    public void Display_positions_round_trip_through_the_codec_the_server_relays_with()
    {
        var sent = Displays(new DisplayDescriptor { Id = "a", Name = "Display 1", Width = 1920, Height = 1080, X = -1280, Y = 200 });

        var received = Assert.IsType<SessionDisplaysMessage>(SignalingCodec.Decode(SignalingCodec.Encode(sent)));

        var display = Assert.Single(received.Displays);
        Assert.Equal(-1280, display.X);
        Assert.Equal(200, display.Y);
    }

    [Fact]
    public void Unknown_positions_stay_off_the_wire_and_older_lists_decode_without_them()
    {
        var withoutPositions = Displays(new DisplayDescriptor { Id = "a", Name = "Display 1", Width = 1920, Height = 1080 });
        var json = Encoding.UTF8.GetString(SignalingCodec.Encode(withoutPositions));
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"y\"", json, StringComparison.Ordinal);

        // A 0.9.82 sharer's list.
        var legacy = Encoding.UTF8.GetBytes(
            "{\"type\":\"session.displays\",\"mid\":\"1\",\"sessionId\":\"s\",\"activeId\":\"a\"," +
            "\"displays\":[{\"id\":\"a\",\"name\":\"Display 1\",\"width\":1920,\"height\":1080}]}");
        var decoded = Assert.IsType<SessionDisplaysMessage>(SignalingCodec.Decode(legacy));
        Assert.Null(Assert.Single(decoded.Displays).X);
    }

    private static SessionDisplaysMessage Displays(DisplayDescriptor display) => new()
    {
        SessionId = "s",
        ActiveDisplayId = display.Id,
        Displays = [display],
    };
}

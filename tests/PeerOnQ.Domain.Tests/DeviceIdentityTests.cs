using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Domain.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void Create_produces_a_complete_identity()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var identity = DeviceIdentity.Create("Office Desktop");

        Assert.NotEqual(Guid.Empty, identity.InternalId);
        Assert.True(PeerOnQId.IsValid(identity.PublicId.Value));
        Assert.Equal("Office Desktop", identity.DisplayName);
        Assert.InRange(identity.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(DeviceIdentity.CurrentIdentityVersion, identity.IdentityVersion);
        Assert.Null(identity.PublicKey);
    }

    [Fact]
    public void Create_trims_the_display_name_and_rejects_blank()
    {
        Assert.Equal("Laptop", DeviceIdentity.Create("  Laptop  ").DisplayName);
        Assert.Throws<ArgumentException>(() => DeviceIdentity.Create("   "));
    }

    [Fact]
    public void Two_identities_never_share_an_id()
    {
        var a = DeviceIdentity.Create("A");
        var b = DeviceIdentity.Create("B");

        Assert.NotEqual(a.InternalId, b.InternalId);
        Assert.NotEqual(a.PublicId, b.PublicId);
    }

    [Fact]
    public void ToLogString_never_leaks_the_full_id()
    {
        var identity = DeviceIdentity.Create("Office Desktop");

        var logLine = identity.ToLogString();

        Assert.DoesNotContain(identity.PublicId.Value, logLine);
        Assert.Contains("***", logLine);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("1", "1")]
    [InlineData("1234", "123-4")]
    [InlineData("438510396069", "438-510-396-069")]
    [InlineData("438-510 396_069", "438-510-396-069")]
    [InlineData("LNK-438-510-396-069-extra-999", "438-510-396-069")]
    public void Display_input_is_grouped_while_the_user_types(string? input, string expected)
    {
        Assert.Equal(expected, PeerOnQId.FormatDisplayInput(input));
    }

    [Fact]
    public void PermissionRequest_exposes_a_masked_id_and_an_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new PermissionRequest
        {
            SessionId = SessionId.New(),
            RequesterId = PeerOnQId.Parse("LNK-483-921-756-204"),
            RequesterDisplayName = "Viewer PC",
            RequestedMode = SessionMode.ViewOnly,
            RequestedAt = now,
            Timeout = TimeSpan.FromSeconds(30),
        };

        Assert.Equal("483-***-***-204", request.MaskedRequesterId);
        Assert.Equal(now.AddSeconds(30), request.ExpiresAt);
        Assert.Equal(SessionMode.ViewOnly, request.RequestedMode);
    }

    [Fact]
    public void Phase4_session_profiles_are_explicit_and_full_control_includes_file_transfer()
    {
        Assert.Equal(
            [SessionMode.ViewOnly, SessionMode.FullControl, SessionMode.FileTransferOnly, SessionMode.Custom],
            Enum.GetValues<SessionMode>());
        var fullControl = SessionPermissionPolicy.ForMode(SessionMode.FullControl);
        Assert.True(fullControl.HasFlag(SessionPermission.ControlInput));
        Assert.True(fullControl.HasFlag(SessionPermission.FileTransfer));
    }
}

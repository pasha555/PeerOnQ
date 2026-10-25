using Avalonia.Input;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

public sealed class WindowsVirtualKeyMapperTests
{
    [Theory]
    [InlineData(Key.A, 0x41, false)]
    [InlineData(Key.D9, 0x39, false)]
    [InlineData(Key.Enter, 0x0D, false)]
    [InlineData(Key.Left, 0x25, true)]
    [InlineData(Key.RightCtrl, 0x11, true)]
    [InlineData(Key.F12, 0x7B, false)]
    public void SupportedKey_MapsToWindowsVirtualKey(Key key, ushort expected, bool extended)
    {
        var mapped = WindowsVirtualKeyMapper.TryMap(key, out var virtualKey, out var isExtended);

        Assert.True(mapped);
        Assert.Equal(expected, virtualKey);
        Assert.Equal(extended, isExtended);
    }

    [Fact]
    public void UnsupportedKey_IsNotForwarded()
    {
        Assert.False(WindowsVirtualKeyMapper.TryMap(Key.None, out _, out _));
    }
}

using PeerOnQ.Application.Collaboration;
using PeerOnQ.Platform.Windows.Security;
using PeerOnQ.Platform.Windows.Input;
using System.Reflection;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class Phase4WindowsIntegrationTests
{
    [Theory]
    [InlineData(0x0259, true)]
    [InlineData(0x0131, false)]
    [InlineData(0xD83D, true)]
    [InlineData(0xDE00, false)]
    public void Unicode_input_uses_the_Windows_packet_contract_without_layout_dependent_virtual_keys(int unit, bool pressed)
    {
        // Verify Win32 KEYBDINPUT values without injecting text into the developer's desktop.
        var factory = typeof(WindowsInputController).GetMethod("CreateUnicodeKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var input = factory.Invoke(null, [(char)unit, pressed])!;
        static object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
        Assert.Equal(1u, Field(input, "Type"));
        var keyboard = Field(Field(input, "Data"), "Keyboard");
        Assert.Equal((ushort)0, Field(keyboard, "VirtualKey"));
        Assert.Equal((ushort)unit, Field(keyboard, "ScanCode"));
        Assert.Equal(pressed ? 0x0004u : 0x0006u, Field(keyboard, "Flags"));
    }

    [Fact]
    public async Task Harmless_received_file_can_be_scanned_through_the_Windows_AMSI_contract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "peeronq-phase4-amsi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "harmless.peeronq.partial");
        await File.WriteAllTextAsync(file, "PeerOnQ harmless integration test content.");
        try
        {
            var result = await new WindowsAmsiMalwareScanner().ScanAsync(file);
            Assert.Contains(result, new[] { MalwareScanResult.Clean, MalwareScanResult.Unavailable });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

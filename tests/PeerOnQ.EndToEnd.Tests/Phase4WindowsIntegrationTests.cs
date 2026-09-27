using PeerOnQ.Application.Collaboration;
using PeerOnQ.Platform.Windows.Security;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class Phase4WindowsIntegrationTests
{
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

using PeerOnQ.Platform.Linux.Security;
using Xunit;

namespace PeerOnQ.Platform.Linux.Tests;

public sealed class SecretServiceIntegrationTests
{
    [LinuxSecretServiceFact]
    public async Task ActualDesktopKeyringRoundTripsSeparatesProfilesAndRemovesSyntheticSecrets()
    {
        var profile = "test-" + Guid.NewGuid().ToString("N");
        var store = new SecretToolDeviceSecretStore(profile);
        var otherProfile = new SecretToolDeviceSecretStore(profile + "-other");
        const string name = "synthetic-test-secret";
        try
        {
            await store.SetAsync(name, [1, 3, 5, 7]);
            Assert.Equal(new byte[] { 1, 3, 5, 7 }, await store.TryGetAsync(name));
            Assert.Null(await otherProfile.TryGetAsync(name));
            await store.RemoveAsync(name);
            Assert.Null(await store.TryGetAsync(name));
        }
        finally
        {
            await store.RemoveAsync(name);
        }
    }

    private sealed class LinuxSecretServiceFactAttribute : FactAttribute
    {
        public LinuxSecretServiceFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PEERONQ_LINUX_NATIVE_TEST") != "1")
                Skip = "Requires an unlocked actual Secret Service session; enable PEERONQ_LINUX_NATIVE_TEST=1.";
        }
    }
}

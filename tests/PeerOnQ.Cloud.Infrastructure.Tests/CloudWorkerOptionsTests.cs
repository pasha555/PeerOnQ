using PeerOnQ.Cloud.Infrastructure;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class CloudWorkerOptionsTests
{
    [Fact]
    public void WorkerLeaseDuration_IsBounded()
    {
        var valid = new CloudWorkerOptions
        {
            EnableSessionReconciliation = true,
            SessionReconciliationInterval = TimeSpan.FromMinutes(1),
            SessionNegotiationTimeout = TimeSpan.FromMinutes(5),
            SessionConnectedInactivityTimeout = TimeSpan.FromMinutes(3),
            SessionReconciliationBatchSize = 500,
            WorkerLeaseDuration = TimeSpan.FromMinutes(2),
        };

        valid.Validate(60);
        var tooShort = new CloudWorkerOptions
        {
            EnableSessionReconciliation = true,
            SessionReconciliationInterval = valid.SessionReconciliationInterval,
            SessionNegotiationTimeout = valid.SessionNegotiationTimeout,
            SessionConnectedInactivityTimeout = valid.SessionConnectedInactivityTimeout,
            SessionReconciliationBatchSize = valid.SessionReconciliationBatchSize,
            WorkerLeaseDuration = TimeSpan.FromSeconds(29),
        };

        Assert.Throws<InvalidOperationException>(() => tooShort.Validate(60));
    }
}

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class ExternalIntegrationFactAttribute : FactAttribute
{
    public ExternalIntegrationFactAttribute(params string[] variables)
    {
        var missing = variables.Where(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))).ToArray();
        if (missing.Length > 0 &&
            !string.Equals(Environment.GetEnvironmentVariable("PEERONQ_REQUIRE_EXTERNAL_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = $"Set {string.Join(", ", missing)} to execute this external integration test.";
    }
}

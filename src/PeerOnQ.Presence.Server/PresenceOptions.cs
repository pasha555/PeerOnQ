using Microsoft.Extensions.Options;

namespace PeerOnQ.Presence.Server;

public sealed class PresenceOptions
{
    public const string SectionName = "PeerOnQ:Presence";

    public int HeartbeatSeconds { get; set; } = 25;
    public int LeaseSeconds { get; set; } = 75;
    public int StaleSweepSeconds { get; set; } = 15;
    public string ServerId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
}

public sealed class PresenceOptionsValidator : IValidateOptions<PresenceOptions>
{
    public ValidateOptionsResult Validate(string? name, PresenceOptions options)
    {
        var errors = new List<string>();
        if (options.HeartbeatSeconds is < 10 or > 60) errors.Add("HeartbeatSeconds must be between 10 and 60.");
        if (options.LeaseSeconds < options.HeartbeatSeconds * 2 || options.LeaseSeconds > 300)
            errors.Add("LeaseSeconds must be at least two heartbeat intervals and no more than 300 seconds.");
        if (options.StaleSweepSeconds is < 5 or > 60) errors.Add("StaleSweepSeconds must be between 5 and 60.");
        if (string.IsNullOrWhiteSpace(options.ServerId) || options.ServerId.Length > 64)
            errors.Add("ServerId is required and must be at most 64 characters.");
        if (string.IsNullOrWhiteSpace(options.Region) || options.Region.Length > 64)
            errors.Add("Region is required and must be at most 64 characters.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

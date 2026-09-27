namespace PeerOnQ.Cloud.Infrastructure.Redis;

public sealed class RedisKeySpace
{
    public string Prefix { get; init; } = "peeronq:cloud:v1";
    public TimeSpan PresenceShadowRetention { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prefix) || Prefix.Length > 64 || Prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '-' or '_')))
            throw new InvalidOperationException("Redis key prefix is invalid.");
        if (PresenceShadowRetention < TimeSpan.FromMinutes(5) || PresenceShadowRetention > TimeSpan.FromDays(1))
            throw new InvalidOperationException("Presence shadow retention is outside the supported range.");
    }
}

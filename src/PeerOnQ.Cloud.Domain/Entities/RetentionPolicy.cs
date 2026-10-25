namespace PeerOnQ.Cloud.Domain.Entities;

/// <summary>
/// Database-owned retention policy. Application runtimes may execute governed retention,
/// but only the deployment policy controller may change these rows.
/// </summary>
public sealed class RetentionPolicy
{
    private RetentionPolicy() { }

    public RetentionRecordKind RecordType { get; private set; }
    public string PolicyVersion { get; private set; } = string.Empty;
    public int RetentionDays { get; private set; }
    public bool Enabled { get; private set; }
    public bool LegalHold { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
}

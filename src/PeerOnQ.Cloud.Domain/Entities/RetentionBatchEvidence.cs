namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class RetentionBatchEvidence
{
    private RetentionBatchEvidence() { }

    public Guid Id { get; private set; }
    public RetentionRecordKind RecordType { get; private set; }
    public string PolicyVersion { get; private set; } = string.Empty;
    public DateTimeOffset CutoffUtc { get; private set; }
    public int RequestedBatchSize { get; private set; }
    public int DeletedCount { get; private set; }
    public DateTimeOffset? OldestRecordAtUtc { get; private set; }
    public DateTimeOffset? NewestRecordAtUtc { get; private set; }
    public string BatchDigestSha256 { get; private set; } = string.Empty;
    public DateTimeOffset ExecutedAtUtc { get; private set; }
}

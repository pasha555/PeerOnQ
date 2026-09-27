using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Application.Collaboration;

public enum TransferItemKind
{
    File = 0,
    Folder = 1,
}

public enum TransferCollisionPolicy
{
    Ask = 0,
    Overwrite = 1,
    Rename = 2,
    Skip = 3,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TransferOffer), "transfer.offer")]
[JsonDerivedType(typeof(TransferAccept), "transfer.accept")]
[JsonDerivedType(typeof(TransferReject), "transfer.reject")]
[JsonDerivedType(typeof(TransferChunk), "transfer.chunk")]
[JsonDerivedType(typeof(TransferPause), "transfer.pause")]
[JsonDerivedType(typeof(TransferResume), "transfer.resume")]
[JsonDerivedType(typeof(TransferCancel), "transfer.cancel")]
[JsonDerivedType(typeof(TransferComplete), "transfer.complete")]
[JsonDerivedType(typeof(TransferFailed), "transfer.failed")]
[JsonDerivedType(typeof(TransferReceipt), "transfer.receipt")]
[JsonDerivedType(typeof(ClipboardTextUpdate), "clipboard.text")]
[JsonDerivedType(typeof(ClipboardStateChange), "clipboard.state")]
[JsonDerivedType(typeof(RemoteInputFocusRequest), "input.focusRequest")]
[JsonDerivedType(typeof(RemoteInputFocusResult), "input.focusResult")]
[JsonDerivedType(typeof(RemoteInputFocusLost), "input.focusLost")]
[JsonDerivedType(typeof(RemoteInputCommand), "input.command")]
[JsonDerivedType(typeof(RemoteInputAcknowledgement), "input.ack")]
[JsonDerivedType(typeof(RemoteInputReleaseAll), "input.releaseAll")]
[JsonDerivedType(typeof(RemoteInputPermissionRevoked), "input.permissionRevoked")]
public abstract record CollaborationMessage
{
    [JsonPropertyName("version")] public int Version { get; init; } = CollaborationProtocolCodec.CurrentVersion;
    [JsonPropertyName("sessionId")] public Guid SessionId { get; init; }
    [JsonPropertyName("permissionGeneration")] public long PermissionGeneration { get; init; }
}

public sealed record TransferEntry
{
    [JsonPropertyName("path")] public required string RelativePath { get; init; }
    [JsonPropertyName("kind")] public required TransferItemKind Kind { get; init; }
    [JsonPropertyName("size")] public required long Size { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("modifiedAt")] public DateTimeOffset? ModifiedAt { get; init; }
}

public sealed record TransferOffer : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
    [JsonPropertyName("entries")] public required IReadOnlyList<TransferEntry> Entries { get; init; }
}

public sealed record TransferAccept : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("collisionPolicy")] public required TransferCollisionPolicy CollisionPolicy { get; init; }
    [JsonPropertyName("receivedOffsets")]
    public IReadOnlyDictionary<string, long> ReceivedOffsets { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}

public sealed record TransferReject : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

public sealed record TransferChunk : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("path")] public required string RelativePath { get; init; }
    [JsonPropertyName("offset")] public required long Offset { get; init; }
    [JsonPropertyName("index")] public required long Index { get; init; }
    [JsonPropertyName("chunkSha256")] public required string ChunkSha256 { get; init; }
    [JsonIgnore] public byte[] Payload { get; init; } = [];
}

public sealed record TransferPause : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
}

public sealed record TransferResume : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("receivedOffsets")]
    public IReadOnlyDictionary<string, long> ReceivedOffsets { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}

public sealed record TransferCancel : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; init; } = "user_canceled";
}

public sealed record TransferComplete : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
}

public sealed record TransferFailed : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

public sealed record TransferReceipt : CollaborationMessage
{
    [JsonPropertyName("transferId")] public required Guid TransferId { get; init; }
    [JsonPropertyName("deliveredBytes")] public required long DeliveredBytes { get; init; }
}

public sealed record ClipboardTextUpdate : CollaborationMessage
{
    [JsonPropertyName("changeId")] public required Guid ChangeId { get; init; }
    [JsonPropertyName("originId")] public required Guid OriginId { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}

public sealed record ClipboardStateChange : CollaborationMessage
{
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
}

public abstract record RemoteInputMessage : CollaborationMessage
{
    [JsonPropertyName("inputVersion")] public required int InputVersion { get; init; }
    [JsonPropertyName("sessionGeneration")] public required long SessionGeneration { get; init; }
    [JsonPropertyName("focusGeneration")] public required long FocusGeneration { get; init; }
    [JsonPropertyName("sequence")] public required long Sequence { get; init; }
}

public sealed record RemoteInputFocusRequest : RemoteInputMessage
{
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
}

public sealed record RemoteInputFocusResult : RemoteInputMessage
{
    [JsonPropertyName("accepted")] public required bool Accepted { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

public sealed record RemoteInputFocusLost : RemoteInputMessage
{
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

public sealed record RemoteInputCommand : RemoteInputMessage
{
    [JsonPropertyName("input")] public required RemoteInputEvent Input { get; init; }
    [JsonPropertyName("measurementSentAtUs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MeasurementSentAtUnixMicroseconds { get; init; }
}

public sealed record RemoteInputAcknowledgement : RemoteInputMessage
{
    [JsonPropertyName("acknowledgedSequence")] public required long AcknowledgedSequence { get; init; }
    [JsonPropertyName("viewerSentAtUs")] public required long ViewerSentAtUnixMicroseconds { get; init; }
    [JsonPropertyName("injectedAtUs")] public required long InjectedAtUnixMicroseconds { get; init; }
}

public sealed record RemoteInputReleaseAll : RemoteInputMessage
{
}

public sealed record RemoteInputPermissionRevoked : RemoteInputMessage
{
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

/// <summary>
/// Versioned wire format. Control frames are bounded JSON. Transfer chunks carry a small JSON
/// header followed by raw bytes, avoiding base64 expansion. No path or clipboard content is
/// logged by this codec.
/// </summary>
public static class CollaborationProtocolCodec
{
    public const int CurrentVersion = 2;
    public const int MaximumControlBytes = 768 * 1024;
    // Large authenticated records keep the high-throughput relay efficient without imposing a
    // bandwidth or file-size cap. The binary relay adds its own bounded per-record envelope.
    public const int MaximumChunkBytes = 256 * 1024;
    public const int MaximumFrameBytes = MaximumControlBytes + MaximumChunkBytes + 16;

    private static readonly byte[] Magic = "PNQ4"u8.ToArray();
    private const byte ControlFrame = 1;
    private const byte ChunkFrame = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Encode(CollaborationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != CurrentVersion)
            throw new InvalidOperationException("Only the current collaboration protocol version can be sent.");
        if (message.SessionId == Guid.Empty || message.PermissionGeneration <= 0)
            throw new InvalidOperationException("Collaboration messages must be bound to a session and permission generation.");

        var isChunk = message is TransferChunk;
        var header = JsonSerializer.SerializeToUtf8Bytes(message, typeof(CollaborationMessage), JsonOptions);
        var payload = message is TransferChunk chunk ? chunk.Payload : [];

        if (header.Length is 0 or > MaximumControlBytes)
            throw new InvalidOperationException("Collaboration control message exceeds the size limit.");
        if (payload.Length > MaximumChunkBytes)
            throw new InvalidOperationException("Transfer chunk exceeds the size limit.");

        var frame = new byte[9 + header.Length + payload.Length];
        Magic.CopyTo(frame, 0);
        frame[4] = isChunk ? ChunkFrame : ControlFrame;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), header.Length);
        header.CopyTo(frame, 9);
        payload.CopyTo(frame, 9 + header.Length);
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out CollaborationMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (frame.Length < 10 || frame.Length > MaximumFrameBytes || !frame[..4].SequenceEqual(Magic))
        {
            error = "invalid_frame";
            return false;
        }

        var frameType = frame[4];
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(5, 4));
        if (frameType is not (ControlFrame or ChunkFrame)
            || headerLength is <= 0 or > MaximumControlBytes
            || 9 + headerLength > frame.Length)
        {
            error = "invalid_header";
            return false;
        }

        var payload = frame[(9 + headerLength)..];
        if ((frameType == ControlFrame && payload.Length != 0)
            || (frameType == ChunkFrame && payload.Length > MaximumChunkBytes))
        {
            error = "invalid_payload";
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<CollaborationMessage>(frame.Slice(9, headerLength), JsonOptions);
            if (message is null || message.Version != CurrentVersion)
            {
                error = "unsupported_version";
                message = null;
                return false;
            }
            if (message.SessionId == Guid.Empty || message.PermissionGeneration <= 0)
            {
                error = "invalid_binding";
                message = null;
                return false;
            }

            if (frameType == ChunkFrame && message is TransferChunk chunk)
            {
                message = chunk with { Payload = payload.ToArray() };
            }
            else if (frameType == ChunkFrame || message is TransferChunk)
            {
                error = "frame_type_mismatch";
                message = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "invalid_json";
            message = null;
            return false;
        }
    }
}

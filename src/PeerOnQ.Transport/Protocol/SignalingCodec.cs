using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerOnQ.Domain.Errors;

namespace PeerOnQ.Transport.Protocol;

/// <summary>
/// JSON codec for the control plane. Frames are hard-capped so a hostile peer cannot push
/// megabytes through the signaling socket. Syntactically valid unknown message types are surfaced
/// as bounded <see cref="UnknownSignalingMessage"/> instances so newer optional frames can be
/// ignored safely without treating arbitrary malformed JSON as compatible.
/// </summary>
public static class SignalingCodec
{
    /// <summary>SDP bodies dominate the size; 64 KB leaves generous head-room.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly HashSet<string> KnownMessageTypes = typeof(SignalingMessage)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
        .Cast<JsonDerivedTypeAttribute>()
        .Select(attribute => attribute.TypeDiscriminator)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    public static byte[] Encode(SignalingMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaxFrameBytes)
        {
            throw new SignalingProtocolException(
                $"Signaling frame of {bytes.Length} bytes exceeds the {MaxFrameBytes} byte limit.");
        }

        return bytes;
    }

    public static string EncodeToString(SignalingMessage message) => Encoding.UTF8.GetString(Encode(message));

    public static SignalingMessage Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
        {
            throw new SignalingProtocolException("Empty signaling frame.");
        }

        if (utf8.Length > MaxFrameBytes)
        {
            throw new SignalingProtocolException(
                $"Signaling frame of {utf8.Length} bytes exceeds the {MaxFrameBytes} byte limit.");
        }

        var payload = utf8.ToArray();
        string? parsedMessageType = null;
        string? parsedMessageId = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new SignalingProtocolException("A signaling frame must be an object with a string type.");
            }

            var messageType = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(messageType)
                || messageType.Length > CapabilityNegotiator.MaxNameLength)
            {
                throw new SignalingProtocolException("The signaling message type is malformed.");
            }

            parsedMessageType = messageType;
            parsedMessageId = document.RootElement.TryGetProperty("mid", out var messageIdElement)
                              && messageIdElement.ValueKind == JsonValueKind.String
                ? messageIdElement.GetString()
                : null;

            if (!KnownMessageTypes.Contains(parsedMessageType))
            {
                return new UnknownSignalingMessage
                {
                    MessageType = parsedMessageType,
                    MessageId = string.IsNullOrWhiteSpace(parsedMessageId) || parsedMessageId.Length > 64
                        ? string.Empty
                        : parsedMessageId,
                };
            }

            return JsonSerializer.Deserialize<SignalingMessage>(payload, Options)
                   ?? throw new SignalingProtocolException("Signaling frame decoded to null.");
        }
        catch (NotSupportedException ex)
        {
            throw new SignalingProtocolException($"Unsupported signaling frame: {ex.Message}");
        }
        catch (SignalingProtocolException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SignalingProtocolException($"Malformed signaling frame: {ex.Message}");
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> utf8, out SignalingMessage? message, out string? error)
    {
        try
        {
            message = Decode(utf8);
            error = null;
            return true;
        }
        catch (SignalingProtocolException ex)
        {
            message = null;
            error = ex.Message;
            return false;
        }
    }
}

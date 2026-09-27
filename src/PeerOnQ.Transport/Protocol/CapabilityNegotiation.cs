using System.Text.Json.Serialization;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Transport.Protocol;

/// <summary>Stable wire identifiers for the native platforms covered by protocol v3.</summary>
public static class PeerOnQClientPlatforms
{
    public const string Windows = "windows";
    public const string Linux = "linux";
    public const string MacOS = "macos";
    public const string Android = "android";
    public const string IOS = "ios";
    public const string IPadOS = "ipados";

    private static readonly HashSet<string> Known =
    [
        Windows,
        Linux,
        MacOS,
        Android,
        IOS,
        IPadOS,
    ];

    public static bool IsKnown(string value) => Known.Contains(value);
}

/// <summary>
/// Endpoint abilities, not product-roadmap labels. A client may advertise only abilities wired
/// into its current native build.
/// </summary>
public static class EndpointCapabilityNames
{
    public const string SessionViewer = "session.viewer";
    public const string SessionHost = "session.host";
    public const string ScreenRender = "screen.render";
    public const string ScreenCapture = "screen.capture";
    public const string InputSend = "input.send";
    public const string InputInject = "input.inject";
    public const string FileSend = "file.send";
    public const string FileReceive = "file.receive";
    public const string ClipboardSend = "clipboard.send";
    public const string ClipboardReceive = "clipboard.receive";
    public const string UnattendedRequest = "unattended.request";
    public const string UnattendedAccept = "unattended.accept";
    public const string SupportInvitationRequest = "support.invitation.request";
    public const string SupportInvitationAccept = "support.invitation.accept";
    public const string SessionReconnect = "session.reconnect";
    public const string DisplaySelectRequest = "display.select.request";
    public const string DisplaySelectAccept = "display.select.accept";
    public const string HybridPostQuantumSecure = "security.hybrid-pq-v1";
}

public static class SignalingServerCapabilityNames
{
    public const string AuthenticatedRegistration = "registration.authenticated";
    public const string SessionRouting = "session.routing";
    public const string SessionCapabilityGate = "session.capability-gate";
    public const string SessionResume = "session.resume";
    public const string Presence = "presence.query";
    public const string TurnCredentials = "turn.credentials";
}

public static class OptionalProtocolFeatureNames
{
    public const string SafeUnknownMessages = "signaling.safe-unknown-message.v1";
    public const string SessionCapabilityDetails = "signaling.session-capability-details.v1";
    public const string FileRelay = "file.relay.v1";
}

/// <summary>Bounded capability declaration sent during the authenticated signaling handshake.</summary>
public sealed record ClientCapabilityManifest
{
    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }

    [JsonPropertyName("optionalFeatures")]
    public IReadOnlyList<string> OptionalFeatures { get; init; } = [];

    [JsonPropertyName("requiredServerCapabilities")]
    public IReadOnlyList<string> RequiredServerCapabilities { get; init; } = [];
}

public sealed record SessionCapabilityNegotiationResult(
    IReadOnlyList<string> MissingRequesterCapabilities,
    IReadOnlyList<string> MissingTargetCapabilities,
    IReadOnlyList<string> NegotiatedOptionalFeatures)
{
    public bool IsCompatible => MissingRequesterCapabilities.Count == 0
                                && MissingTargetCapabilities.Count == 0;
}

/// <summary>Pure, platform-neutral validation and least-capability session negotiation.</summary>
public static class CapabilityNegotiator
{
    public const int MaxCapabilities = 64;
    public const int MaxOptionalFeatures = 32;
    public const int MaxRequiredServerCapabilities = 16;
    public const int MaxNameLength = 64;

    public static IReadOnlyList<string> ServerCapabilities { get; } =
    [
        SignalingServerCapabilityNames.AuthenticatedRegistration,
        SignalingServerCapabilityNames.Presence,
        SignalingServerCapabilityNames.SessionCapabilityGate,
        SignalingServerCapabilityNames.SessionResume,
        SignalingServerCapabilityNames.SessionRouting,
        SignalingServerCapabilityNames.TurnCredentials,
    ];

    public static IReadOnlyList<string> ServerOptionalFeatures { get; } =
    [
        OptionalProtocolFeatureNames.FileRelay,
        OptionalProtocolFeatureNames.SafeUnknownMessages,
        OptionalProtocolFeatureNames.SessionCapabilityDetails,
    ];

    public static bool TryNormalize(
        ClientCapabilityManifest? manifest,
        out ClientCapabilityManifest normalized,
        out string error)
    {
        normalized = new ClientCapabilityManifest
        {
            Platform = string.Empty,
            Capabilities = [],
        };

        if (manifest is null)
        {
            error = "Protocol v3 requires a client capability manifest.";
            return false;
        }

        if (!PeerOnQClientPlatforms.IsKnown(manifest.Platform))
        {
            error = "The client platform identifier is unsupported.";
            return false;
        }

        if (!TryNormalizeNames(
                manifest.Capabilities,
                MaxCapabilities,
                "capability",
                out var capabilities,
                out error)
            || !TryNormalizeNames(
                manifest.OptionalFeatures,
                MaxOptionalFeatures,
                "optional feature",
                out var optionalFeatures,
                out error)
            || !TryNormalizeNames(
                manifest.RequiredServerCapabilities,
                MaxRequiredServerCapabilities,
                "required server capability",
                out var requiredServerCapabilities,
                out error))
        {
            return false;
        }

        var set = capabilities.ToHashSet(StringComparer.Ordinal);
        if (!ValidateDependency(set, EndpointCapabilityNames.ScreenCapture, EndpointCapabilityNames.SessionHost, out error)
            || !ValidateDependency(set, EndpointCapabilityNames.ScreenRender, EndpointCapabilityNames.SessionViewer, out error)
            || !ValidateDependencies(
                set,
                EndpointCapabilityNames.InputInject,
                [EndpointCapabilityNames.SessionHost, EndpointCapabilityNames.ScreenCapture],
                out error)
            || !ValidateDependencies(
                set,
                EndpointCapabilityNames.InputSend,
                [EndpointCapabilityNames.SessionViewer, EndpointCapabilityNames.ScreenRender],
                out error)
            || !ValidateDependency(set, EndpointCapabilityNames.UnattendedAccept, EndpointCapabilityNames.SessionHost, out error)
            || !ValidateDependency(set, EndpointCapabilityNames.UnattendedRequest, EndpointCapabilityNames.SessionViewer, out error)
            || !ValidateDependency(set, EndpointCapabilityNames.SupportInvitationAccept, EndpointCapabilityNames.SessionHost, out error)
            || !ValidateDependency(set, EndpointCapabilityNames.SupportInvitationRequest, EndpointCapabilityNames.SessionViewer, out error)
            || !ValidateDependencies(
                set,
                EndpointCapabilityNames.DisplaySelectAccept,
                [EndpointCapabilityNames.SessionHost, EndpointCapabilityNames.ScreenCapture],
                out error)
            || !ValidateDependencies(
                set,
                EndpointCapabilityNames.DisplaySelectRequest,
                [EndpointCapabilityNames.SessionViewer, EndpointCapabilityNames.ScreenRender],
                out error))
        {
            return false;
        }

        normalized = manifest with
        {
            Capabilities = capabilities,
            OptionalFeatures = optionalFeatures,
            RequiredServerCapabilities = requiredServerCapabilities,
        };
        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<string> MissingServerCapabilities(ClientCapabilityManifest manifest) =>
        manifest.RequiredServerCapabilities
            .Except(ServerCapabilities, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> NegotiateServerFeatures(ClientCapabilityManifest manifest) =>
        manifest.OptionalFeatures
            .Intersect(ServerOptionalFeatures, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static SessionCapabilityNegotiationResult NegotiateSession(
        ClientCapabilityManifest requester,
        ClientCapabilityManifest target,
        SessionPermission permissions,
        SessionAccessKind accessKind)
    {
        var requesterRequired = new HashSet<string>(StringComparer.Ordinal)
        {
            EndpointCapabilityNames.SessionViewer,
            EndpointCapabilityNames.HybridPostQuantumSecure,
        };
        var targetRequired = new HashSet<string>(StringComparer.Ordinal)
        {
            EndpointCapabilityNames.SessionHost,
            EndpointCapabilityNames.HybridPostQuantumSecure,
        };

        if (permissions.HasFlag(SessionPermission.ViewScreen))
        {
            requesterRequired.Add(EndpointCapabilityNames.ScreenRender);
            targetRequired.Add(EndpointCapabilityNames.ScreenCapture);
        }

        if (permissions.HasFlag(SessionPermission.ControlInput))
        {
            requesterRequired.Add(EndpointCapabilityNames.InputSend);
            targetRequired.Add(EndpointCapabilityNames.InputInject);
        }

        if (permissions.HasFlag(SessionPermission.FileTransfer))
        {
            requesterRequired.Add(EndpointCapabilityNames.FileSend);
            requesterRequired.Add(EndpointCapabilityNames.FileReceive);
            targetRequired.Add(EndpointCapabilityNames.FileSend);
            targetRequired.Add(EndpointCapabilityNames.FileReceive);
        }

        if (permissions.HasFlag(SessionPermission.ClipboardText))
        {
            requesterRequired.Add(EndpointCapabilityNames.ClipboardSend);
            requesterRequired.Add(EndpointCapabilityNames.ClipboardReceive);
            targetRequired.Add(EndpointCapabilityNames.ClipboardSend);
            targetRequired.Add(EndpointCapabilityNames.ClipboardReceive);
        }

        if (accessKind == SessionAccessKind.Unattended)
        {
            requesterRequired.Add(EndpointCapabilityNames.UnattendedRequest);
            targetRequired.Add(EndpointCapabilityNames.UnattendedAccept);
        }
        else if (accessKind == SessionAccessKind.SupportInvitation)
        {
            requesterRequired.Add(EndpointCapabilityNames.SupportInvitationRequest);
            targetRequired.Add(EndpointCapabilityNames.SupportInvitationAccept);
        }

        var requesterCapabilities = requester.Capabilities.ToHashSet(StringComparer.Ordinal);
        var targetCapabilities = target.Capabilities.ToHashSet(StringComparer.Ordinal);
        return new SessionCapabilityNegotiationResult(
            requesterRequired.Except(requesterCapabilities).Order(StringComparer.Ordinal).ToArray(),
            targetRequired.Except(targetCapabilities).Order(StringComparer.Ordinal).ToArray(),
            requester.OptionalFeatures
                .Intersect(target.OptionalFeatures, StringComparer.Ordinal)
                .Intersect(ServerOptionalFeatures, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool TryNormalizeNames(
        IReadOnlyList<string>? source,
        int maximumCount,
        string kind,
        out string[] normalized,
        out string error)
    {
        normalized = [];
        if (source is null || source.Count > maximumCount)
        {
            error = $"The {kind} list is missing or exceeds {maximumCount} entries.";
            return false;
        }

        if (source.Any(value => !IsValidName(value)))
        {
            error = $"A {kind} name is malformed.";
            return false;
        }

        if (source.Distinct(StringComparer.Ordinal).Count() != source.Count)
        {
            error = $"The {kind} list contains duplicates.";
            return false;
        }

        normalized = source.Order(StringComparer.Ordinal).ToArray();
        error = string.Empty;
        return true;
    }

    private static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxNameLength
        && value.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '-');

    private static bool ValidateDependency(
        IReadOnlySet<string> capabilities,
        string capability,
        string dependency,
        out string error) =>
        ValidateDependencies(capabilities, capability, [dependency], out error);

    private static bool ValidateDependencies(
        IReadOnlySet<string> capabilities,
        string capability,
        IReadOnlyList<string> dependencies,
        out string error)
    {
        var missing = capabilities.Contains(capability)
            ? dependencies.Where(dependency => !capabilities.Contains(dependency)).ToArray()
            : [];
        if (missing.Length == 0)
        {
            error = string.Empty;
            return true;
        }

        error = $"Capability '{capability}' requires: {string.Join(", ", missing)}.";
        return false;
    }
}

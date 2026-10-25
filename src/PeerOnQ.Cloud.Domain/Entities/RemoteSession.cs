namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class RemoteSession
{
    private RemoteSession() { }

    private RemoteSession(
        Guid id,
        Guid viewerDeviceId,
        Guid hostDeviceId,
        PermissionMode permissionMode,
        string serverRegion,
        string? clientVersionViewer,
        string? clientVersionHost,
        DateTimeOffset startedAtUtc,
        DateTimeOffset lastActivityAtUtc)
    {
        Id = id;
        ViewerDeviceId = viewerDeviceId;
        HostDeviceId = hostDeviceId;
        PermissionMode = permissionMode;
        ConnectionPath = ConnectionPath.Unknown;
        ServerRegion = serverRegion;
        ClientVersionViewer = clientVersionViewer;
        ClientVersionHost = clientVersionHost;
        StartedAtUtc = startedAtUtc;
        LastActivityAtUtc = lastActivityAtUtc;
        Lifecycle = SessionLifecycle.Starting;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid ViewerDeviceId { get; private set; }
    public Guid HostDeviceId { get; private set; }
    public PermissionMode PermissionMode { get; private set; }
    public ConnectionPath ConnectionPath { get; private set; }
    public string ServerRegion { get; private set; } = string.Empty;
    public string? ClientVersionViewer { get; private set; }
    public string? ClientVersionHost { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset LastActivityAtUtc { get; private set; }
    public Guid? LastActivityEventId { get; private set; }
    public DateTimeOffset? ConnectedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    public SessionEndReason? EndReason { get; private set; }
    public string? FailureStage { get; private set; }
    public string? FailureCode { get; private set; }
    public bool UsedTurn { get; private set; }
    public int ReconnectCount { get; private set; }
    public SessionLifecycle Lifecycle { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public static RemoteSession Start(
        Guid id,
        Guid viewerDeviceId,
        Guid hostDeviceId,
        PermissionMode permissionMode,
        string serverRegion,
        string? clientVersionViewer,
        string? clientVersionHost,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? observedAtUtc = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(id));
        if (viewerDeviceId == Guid.Empty || hostDeviceId == Guid.Empty)
            throw new ArgumentException("Viewer and host device IDs are required.");

        return new RemoteSession(
            id,
            viewerDeviceId,
            hostDeviceId,
            permissionMode,
            Device.NormalizeRequired(serverRegion, 64, nameof(serverRegion)),
            NormalizeOptional(clientVersionViewer, 64),
            NormalizeOptional(clientVersionHost, 64),
            startedAtUtc,
            observedAtUtc ?? startedAtUtc);
    }

    public void ReportParticipant(Guid deviceId, string clientVersion)
    {
        var normalized = Device.NormalizeRequired(clientVersion, 64, nameof(clientVersion));
        if (deviceId == ViewerDeviceId)
        {
            if (ClientVersionViewer == normalized) return;
            ClientVersionViewer = normalized;
        }
        else if (deviceId == HostDeviceId)
        {
            if (ClientVersionHost == normalized) return;
            ClientVersionHost = normalized;
        }
        else throw new InvalidOperationException("The reporting device is not a session participant.");
        ConcurrencyVersion++;
    }

    public void MarkConnected(
        ConnectionPath path,
        bool usedTurn,
        DateTimeOffset connectedAtUtc,
        DateTimeOffset? observedAtUtc = null)
    {
        if (Lifecycle != SessionLifecycle.Starting) throw new InvalidOperationException("Only a starting session can connect.");
        var knownRelayPath = path is ConnectionPath.TurnUdp or ConnectionPath.TurnTcp or ConnectionPath.TurnTls;
        var knownDirectPath = path is ConnectionPath.LanDirect or ConnectionPath.InternetDirect;
        if ((knownRelayPath && !usedTurn) || (knownDirectPath && usedTurn))
            throw new ArgumentException("TURN usage must agree with the selected connection path.", nameof(usedTurn));
        if (connectedAtUtc < StartedAtUtc) throw new ArgumentOutOfRangeException(nameof(connectedAtUtc));

        ConnectionPath = path;
        UsedTurn = usedTurn;
        ConnectedAtUtc = connectedAtUtc;
        LastActivityAtUtc = observedAtUtc ?? connectedAtUtc;
        Lifecycle = SessionLifecycle.Connected;
        ConcurrencyVersion++;
    }

    public bool RecordActivity(Guid participantDeviceId, Guid eventId, DateTimeOffset observedAtUtc)
    {
        if (participantDeviceId != ViewerDeviceId && participantDeviceId != HostDeviceId)
            throw new InvalidOperationException("The reporting device is not a session participant.");
        if (eventId == Guid.Empty) throw new ArgumentException("Activity event ID is required.", nameof(eventId));
        if (Lifecycle != SessionLifecycle.Connected)
            throw new InvalidOperationException("Only a connected session can report activity.");
        if (LastActivityEventId == eventId) return false;

        LastActivityAtUtc = observedAtUtc;
        LastActivityEventId = eventId;
        ConcurrencyVersion++;
        return true;
    }

    public void End(
        SessionEndReason reason,
        string? failureStage,
        string? failureCode,
        int reconnectCount,
        DateTimeOffset endedAtUtc)
    {
        if (Lifecycle is SessionLifecycle.Ended or SessionLifecycle.Failed) return;
        if (endedAtUtc < StartedAtUtc) throw new ArgumentOutOfRangeException(nameof(endedAtUtc));
        if (reconnectCount < 0) throw new ArgumentOutOfRangeException(nameof(reconnectCount));

        EndReason = reason;
        FailureStage = NormalizeOptional(failureStage, 64);
        FailureCode = NormalizeOptional(failureCode, 128);
        ReconnectCount = reconnectCount;
        EndedAtUtc = endedAtUtc;
        Lifecycle = reason == SessionEndReason.Completed || reason == SessionEndReason.Cancelled
            ? SessionLifecycle.Ended
            : SessionLifecycle.Failed;
        ConcurrencyVersion++;
    }

    public void MarkStale(DateTimeOffset endedAtUtc)
    {
        if (Lifecycle is SessionLifecycle.Ended or SessionLifecycle.Failed or SessionLifecycle.Stale) return;
        EndedAtUtc = endedAtUtc;
        EndReason = SessionEndReason.TimedOut;
        FailureStage = "Lifecycle";
        FailureCode = "SESSION_STALE";
        Lifecycle = SessionLifecycle.Stale;
        ConcurrencyVersion++;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

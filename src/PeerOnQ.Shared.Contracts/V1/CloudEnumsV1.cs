using System.Text.Json.Serialization;

namespace PeerOnQ.Shared.Contracts.V1;

[JsonConverter(typeof(JsonStringEnumConverter<PlatformKindV1>))]
public enum PlatformKindV1 { Windows, MacOS, Linux, Android, IOS }

[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureKindV1>))]
public enum ArchitectureKindV1 { X64, Arm64, X86, Arm }

[JsonConverter(typeof(JsonStringEnumConverter<InstallChannelV1>))]
public enum InstallChannelV1 { Stable, Beta, Enterprise, Development }

[JsonConverter(typeof(JsonStringEnumConverter<PresenceStateV1>))]
public enum PresenceStateV1
{
    Connecting,
    Online,
    Busy,
    InSession,
    Reconnecting,
    Offline,
    Blocked,
    UnsupportedVersion
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionPathV1>))]
public enum ConnectionPathV1 { LanDirect, InternetDirect, TurnUdp, TurnTcp, TurnTls, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<PermissionModeV1>))]
public enum PermissionModeV1 { ViewOnly, FullControl, FileTransfer, Custom }

[JsonConverter(typeof(JsonStringEnumConverter<DownloadResultV1>))]
public enum DownloadResultV1 { Started, Completed, Partial, Cancelled, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<UpdateEventKindV1>))]
public enum UpdateEventKindV1 { Offered, Downloaded, Installed, Failed, RolledBack }

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticStatusV1>))]
public enum DiagnosticStatusV1 { AwaitingUpload, Uploaded, Processing, Available, Rejected, Deleting, Expired, Deleted }

[JsonConverter(typeof(JsonStringEnumConverter<SessionEndReasonV1>))]
public enum SessionEndReasonV1 { Completed, Cancelled, NetworkLost, AuthenticationFailed, PermissionDenied, Error, TimedOut }

[JsonConverter(typeof(JsonStringEnumConverter<SessionEventKindV1>))]
public enum SessionEventKindV1 { Started, Connected, Ended }

[JsonConverter(typeof(JsonStringEnumConverter<SessionParticipantRoleV1>))]
public enum SessionParticipantRoleV1 { Viewer, Host }

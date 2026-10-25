using System.Text.Json.Serialization;

namespace PeerOnQ.Cloud.Domain;

public enum PlatformKind { Windows, MacOS, Linux, Android, IOS }
public enum ArchitectureKind { X64, Arm64, X86, Arm }
public enum InstallChannel { Stable, Beta, Enterprise, Development }
public enum PresenceState { Connecting, Online, Busy, InSession, Reconnecting, Offline, Blocked, UnsupportedVersion }
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionPath>))]
public enum ConnectionPath { LanDirect, InternetDirect, TurnUdp, TurnTcp, TurnTls, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter<PermissionMode>))]
public enum PermissionMode { ViewOnly, FullControl, FileTransfer, Custom }
[JsonConverter(typeof(JsonStringEnumConverter<SessionLifecycle>))]
public enum SessionLifecycle { Starting, Connected, Ended, Failed, Stale }
public enum SessionEndReason { Completed, Cancelled, NetworkLost, AuthenticationFailed, PermissionDenied, Error, TimedOut }
public enum DownloadResult { Started, Completed, Partial, Cancelled, Failed }
public enum UpdateEventKind { Offered, Downloaded, Installed, Failed, RolledBack }
public enum DiagnosticStatus { AwaitingUpload, Uploaded, Processing, Available, Rejected, Deleting, Expired, Deleted }
public enum AdminRoleKind { Owner, SecurityAdministrator, OperationsAdministrator, SupportAgent, ReleaseManager, ReadOnlyAnalyst }
[JsonConverter(typeof(JsonStringEnumConverter<AuditResult>))]
public enum AuditResult { Succeeded, Failed, Denied }
public enum AlertSeverity { Information, Warning, Critical }
public enum ServiceHealthState { Healthy, Degraded, Unhealthy, Unknown }
public enum RetentionRecordKind { AuditEvents, AlertEvents }
[JsonConverter(typeof(JsonStringEnumConverter<CustomerAccountStatus>))]
public enum CustomerAccountStatus { Active, Disabled, DeletionPending, Deleted }
[JsonConverter(typeof(JsonStringEnumConverter<CustomerRoleKind>))]
public enum CustomerRoleKind { Owner, Administrator, Technician, Member, Auditor }
public enum CustomerRegistrationMode { Closed, InvitationOnly, Open }
public enum CustomerTokenPurpose { EmailVerification, PasswordReset }
[JsonConverter(typeof(JsonStringEnumConverter<AccountDataRequestKind>))]
public enum AccountDataRequestKind { Export, Delete }
[JsonConverter(typeof(JsonStringEnumConverter<AccountDataRequestStatus>))]
public enum AccountDataRequestStatus { Pending, Completed, Rejected }

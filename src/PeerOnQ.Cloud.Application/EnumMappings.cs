using PeerOnQ.Cloud.Domain;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application;

internal static class EnumMappings
{
    public static PlatformKind ToDomain(this PlatformKindV1 value) => value switch
    {
        PlatformKindV1.Windows => PlatformKind.Windows,
        PlatformKindV1.MacOS => PlatformKind.MacOS,
        PlatformKindV1.Linux => PlatformKind.Linux,
        PlatformKindV1.Android => PlatformKind.Android,
        PlatformKindV1.IOS => PlatformKind.IOS,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Platform is invalid.")
    };

    public static ArchitectureKind ToDomain(this ArchitectureKindV1 value) => value switch
    {
        ArchitectureKindV1.X64 => ArchitectureKind.X64,
        ArchitectureKindV1.Arm64 => ArchitectureKind.Arm64,
        ArchitectureKindV1.X86 => ArchitectureKind.X86,
        ArchitectureKindV1.Arm => ArchitectureKind.Arm,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Architecture is invalid.")
    };

    public static InstallChannel ToDomain(this InstallChannelV1 value) => value switch
    {
        InstallChannelV1.Stable => InstallChannel.Stable,
        InstallChannelV1.Beta => InstallChannel.Beta,
        InstallChannelV1.Enterprise => InstallChannel.Enterprise,
        InstallChannelV1.Development => InstallChannel.Development,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Install channel is invalid.")
    };

    public static PermissionMode ToDomain(this PermissionModeV1 value) => value switch
    {
        PermissionModeV1.ViewOnly => PermissionMode.ViewOnly,
        PermissionModeV1.FullControl => PermissionMode.FullControl,
        PermissionModeV1.FileTransfer => PermissionMode.FileTransfer,
        PermissionModeV1.Custom => PermissionMode.Custom,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Permission mode is invalid.")
    };

    public static ConnectionPath ToDomain(this ConnectionPathV1 value) => value switch
    {
        ConnectionPathV1.LanDirect => ConnectionPath.LanDirect,
        ConnectionPathV1.InternetDirect => ConnectionPath.InternetDirect,
        ConnectionPathV1.TurnUdp => ConnectionPath.TurnUdp,
        ConnectionPathV1.TurnTcp => ConnectionPath.TurnTcp,
        ConnectionPathV1.TurnTls => ConnectionPath.TurnTls,
        ConnectionPathV1.Unknown => ConnectionPath.Unknown,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Connection path is invalid.")
    };

    public static SessionEndReason ToDomain(this SessionEndReasonV1 value) => value switch
    {
        SessionEndReasonV1.Completed => SessionEndReason.Completed,
        SessionEndReasonV1.Cancelled => SessionEndReason.Cancelled,
        SessionEndReasonV1.NetworkLost => SessionEndReason.NetworkLost,
        SessionEndReasonV1.AuthenticationFailed => SessionEndReason.AuthenticationFailed,
        SessionEndReasonV1.PermissionDenied => SessionEndReason.PermissionDenied,
        SessionEndReasonV1.Error => SessionEndReason.Error,
        SessionEndReasonV1.TimedOut => SessionEndReason.TimedOut,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Session end reason is invalid.")
    };

    public static DownloadResult ToDomain(this DownloadResultV1 value) => value switch
    {
        DownloadResultV1.Started => DownloadResult.Started,
        DownloadResultV1.Completed => DownloadResult.Completed,
        DownloadResultV1.Partial => DownloadResult.Partial,
        DownloadResultV1.Cancelled => DownloadResult.Cancelled,
        DownloadResultV1.Failed => DownloadResult.Failed,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Download result is invalid.")
    };

    public static UpdateEventKind ToDomain(this UpdateEventKindV1 value) => value switch
    {
        UpdateEventKindV1.Offered => UpdateEventKind.Offered,
        UpdateEventKindV1.Downloaded => UpdateEventKind.Downloaded,
        UpdateEventKindV1.Installed => UpdateEventKind.Installed,
        UpdateEventKindV1.Failed => UpdateEventKind.Failed,
        UpdateEventKindV1.RolledBack => UpdateEventKind.RolledBack,
        _ => throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Update event type is invalid.")
    };
}

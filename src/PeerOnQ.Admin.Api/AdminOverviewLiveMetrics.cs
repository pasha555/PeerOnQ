using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Admin.Api;

public sealed record AdminOverviewLiveMetrics(long? OnlineDevices, long? ActiveSessions);

public static class AdminOverviewLiveMetricsOverlay
{
    public static AdminOverviewV1 Apply(AdminOverviewV1 persisted, AdminOverviewLiveMetrics live) =>
        persisted with
        {
            OnlineDevices = live.OnlineDevices is { } online
                ? Math.Max(persisted.OnlineDevices, online)
                : persisted.OnlineDevices,
            ActiveDevicesToday = live.OnlineDevices is { } activeToday
                ? Math.Max(persisted.ActiveDevicesToday, activeToday)
                : persisted.ActiveDevicesToday,
            ActiveDevicesThisMonth = live.OnlineDevices is { } activeThisMonth
                ? Math.Max(persisted.ActiveDevicesThisMonth, activeThisMonth)
                : persisted.ActiveDevicesThisMonth,
            ActiveSessions = live.ActiveSessions is { } sessions
                ? Math.Max(persisted.ActiveSessions, sessions)
                : persisted.ActiveSessions,
        };
}

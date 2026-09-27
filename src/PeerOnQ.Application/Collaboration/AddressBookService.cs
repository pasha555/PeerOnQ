using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;

namespace PeerOnQ.Application.Collaboration;

public enum DevicePresence
{
    Unknown = 0,
    Offline = 1,
    Online = 2,
}

public interface IDevicePresenceProvider
{
    Task<IReadOnlyDictionary<PeerOnQId, DevicePresence>> QueryAsync(
        IReadOnlyList<PeerOnQId> deviceIds,
        CancellationToken cancellationToken = default);
}

public sealed class UnknownDevicePresenceProvider : IDevicePresenceProvider
{
    public static UnknownDevicePresenceProvider Instance { get; } = new();
    public Task<IReadOnlyDictionary<PeerOnQId, DevicePresence>> QueryAsync(
        IReadOnlyList<PeerOnQId> deviceIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<PeerOnQId, DevicePresence>>(
            deviceIds.Distinct().ToDictionary(id => id, _ => DevicePresence.Unknown));
}

public sealed record AddressBookItem(AddressBookDevice Device, DevicePresence Presence);

public sealed class AddressBookService(
    ICollaborationProfileStore store,
    IDevicePresenceProvider? presenceProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDevicePresenceProvider _presence = presenceProvider ?? UnknownDevicePresenceProvider.Instance;

    public async Task<IReadOnlyList<AddressBookGroup>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        (await store.LoadAsync(cancellationToken)).AddressBookGroups
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<AddressBookDevice> SaveDeviceAsync(AddressBookDevice device, CancellationToken cancellationToken = default)
    {
        var normalized = device with
        {
            RecordId = device.RecordId == Guid.Empty ? Guid.NewGuid() : device.RecordId,
            DisplayName = NormalizeText(device.DisplayName, 80, required: true),
            // OS and last-seen are authenticated observations, never editable address-book claims.
            OperatingSystem = null,
            OperatingSystemVerifiedAt = null,
            LastSeenAt = null,
            LastSeenVerifiedAt = null,
            Notes = NormalizeText(device.Notes, 2_000, required: false),
            Tags = device.Tags.Select(tag => NormalizeText(tag, 40, required: true))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray(),
            GroupIds = device.GroupIds.Distinct().ToArray(),
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.AddressBookDevices.ToList();
            var duplicate = devices.FindIndex(item => item.DeviceId == normalized.DeviceId);
            if (duplicate >= 0 && devices[duplicate].RecordId != normalized.RecordId)
                normalized = normalized with { RecordId = devices[duplicate].RecordId };
            var index = devices.FindIndex(item => item.RecordId == normalized.RecordId);
            if (index >= 0)
            {
                var authenticated = devices[index];
                normalized = normalized with
                {
                    OperatingSystem = authenticated.OperatingSystem,
                    OperatingSystemVerifiedAt = authenticated.OperatingSystemVerifiedAt,
                    LastSeenAt = authenticated.LastSeenAt,
                    LastSeenVerifiedAt = authenticated.LastSeenVerifiedAt,
                };
            }
            if (index >= 0) devices[index] = normalized; else devices.Add(normalized);
            await store.SaveAsync(profile with { AddressBookDevices = devices }, cancellationToken);
            return normalized;
        }
        finally { _gate.Release(); }
    }

    public async Task<AddressBookDevice> RecordConnectedDeviceAsync(
        PeerOnQId deviceId,
        string peerDisplayName,
        DateTimeOffset connectedAt,
        CancellationToken cancellationToken = default)
    {
        var normalizedPeerName = NormalizeText(peerDisplayName, 80, required: true);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.AddressBookDevices.ToList();
            var index = devices.FindIndex(item => item.DeviceId == deviceId);
            var connectedDevice = index >= 0
                ? devices[index] with
                {
                    LastSeenAt = connectedAt,
                    LastSeenVerifiedAt = connectedAt,
                }
                : new AddressBookDevice
                {
                    RecordId = Guid.NewGuid(),
                    DeviceId = deviceId,
                    DisplayName = normalizedPeerName,
                    LastSeenAt = connectedAt,
                    LastSeenVerifiedAt = connectedAt,
                };

            if (index >= 0) devices[index] = connectedDevice; else devices.Add(connectedDevice);
            await store.SaveAsync(profile with { AddressBookDevices = devices }, cancellationToken);
            return connectedDevice;
        }
        finally { _gate.Release(); }
    }

    public async Task<AddressBookGroup> SaveGroupAsync(AddressBookGroup group, CancellationToken cancellationToken = default)
    {
        var normalized = group with
        {
            GroupId = group.GroupId == Guid.Empty ? Guid.NewGuid() : group.GroupId,
            Name = NormalizeText(group.Name, 80, required: true),
        };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var groups = profile.AddressBookGroups.ToList();
            if (groups.Any(item => item.GroupId != normalized.GroupId
                                   && string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A group with that name already exists.");
            var index = groups.FindIndex(item => item.GroupId == normalized.GroupId);
            if (index >= 0) groups[index] = normalized; else groups.Add(normalized);
            await store.SaveAsync(profile with { AddressBookGroups = groups }, cancellationToken);
            return normalized;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AddressBookItem>> SearchAsync(
        string? query = null,
        Guid? groupId = null,
        bool favoritesOnly = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.LoadAsync(cancellationToken);
        IEnumerable<AddressBookDevice> devices = profile.AddressBookDevices;
        if (groupId is not null) devices = devices.Where(item => item.GroupIds.Contains(groupId.Value));
        if (favoritesOnly) devices = devices.Where(item => item.IsFavorite);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            devices = devices.Where(item =>
                item.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.DeviceId.Value.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (item.OperatingSystemVerifiedAt is not null
                    && (item.OperatingSystem?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                || item.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (item.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = devices.OrderByDescending(item => item.IsFavorite)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var presence = await _presence.QueryAsync(ordered.Select(item => item.DeviceId).ToArray(), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var result = ordered.Select(item =>
        {
            var state = presence.GetValueOrDefault(item.DeviceId, DevicePresence.Unknown);
            var visible = item with
            {
                OperatingSystem = item.OperatingSystemVerifiedAt is null ? null : item.OperatingSystem,
                LastSeenAt = item.LastSeenVerifiedAt is null ? null : item.LastSeenAt,
            };
            return new AddressBookItem(
                state == DevicePresence.Online
                    ? visible with { LastSeenAt = now, LastSeenVerifiedAt = now }
                    : visible,
                state);
        }).ToArray();

        if (result.Any(item => item.Presence == DevicePresence.Online))
            await PersistLastSeenAsync(result, cancellationToken);
        return result;
    }

    private async Task PersistLastSeenAsync(IReadOnlyList<AddressBookItem> observed, CancellationToken cancellationToken)
    {
        var updates = observed
            .Where(item => item.Presence == DevicePresence.Online && item.Device.LastSeenAt is not null)
            .ToDictionary(item => item.Device.RecordId, item => item.Device.LastSeenAt!.Value);
        if (updates.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.AddressBookDevices.Select(device =>
                updates.TryGetValue(device.RecordId, out var lastSeen)
                    ? device with { LastSeenAt = lastSeen, LastSeenVerifiedAt = lastSeen }
                    : device).ToArray();
            await store.SaveAsync(profile with { AddressBookDevices = devices }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveDeviceAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            await store.SaveAsync(profile with
            {
                AddressBookDevices = profile.AddressBookDevices.Where(item => item.RecordId != recordId).ToArray(),
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            await store.SaveAsync(profile with
            {
                AddressBookGroups = profile.AddressBookGroups.Where(group => group.GroupId != groupId).ToArray(),
                AddressBookDevices = profile.AddressBookDevices.Select(device => device with
                {
                    GroupIds = device.GroupIds.Where(id => id != groupId).ToArray(),
                }).ToArray(),
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static string NormalizeText(string? value, int maximumLength, bool required)
    {
        var clean = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(maximumLength).ToArray()).Trim();
        if (required && clean.Length == 0) throw new ArgumentException("A value is required.", nameof(value));
        return clean;
    }
}

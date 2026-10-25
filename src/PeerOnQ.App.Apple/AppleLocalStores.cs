using System.Text.Json;
using Foundation;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App.Apple;

public sealed class AppleDeviceIdentityRepository(NSUserDefaults defaults) : IDeviceIdentityRepository
{
    private const string StorageKey = "peeronq.device-identity.v1";

    public Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = defaults.StringForKey(StorageKey);
        if (json is null) return Task.FromResult<DeviceIdentity?>(null);
        var stored = JsonSerializer.Deserialize<StoredIdentity>(json)
                     ?? throw new InvalidDataException("The Apple device identity is malformed.");
        return Task.FromResult<DeviceIdentity?>(new DeviceIdentity
        {
            InternalId = stored.InternalId,
            PublicId = PeerOnQId.Parse(stored.PublicId),
            DisplayName = stored.DisplayName,
            CreatedAt = stored.CreatedAt,
            IdentityVersion = stored.IdentityVersion,
            PublicIdServerAssigned = stored.PublicIdServerAssigned,
            PublicKey = stored.PublicKey,
        });
    }

    public Task SaveAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = new StoredIdentity(
            identity.InternalId,
            identity.PublicId.ToString(),
            identity.DisplayName,
            identity.CreatedAt,
            identity.IdentityVersion,
            identity.PublicIdServerAssigned,
            identity.PublicKey);
        defaults.SetString(JsonSerializer.Serialize(stored), StorageKey);
        return Task.CompletedTask;
    }

    private sealed record StoredIdentity(
        Guid InternalId,
        string PublicId,
        string DisplayName,
        DateTimeOffset CreatedAt,
        int IdentityVersion,
        bool PublicIdServerAssigned,
        string? PublicKey);
}

public sealed class AppleBlockedDeviceStore(NSUserDefaults defaults) : IBlockedDeviceStore
{
    private const string StorageKey = "peeronq.blocked-devices.v1";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> IsBlockedAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Load().Contains(id.ToString()); }
        finally { _gate.Release(); }
    }

    public async Task BlockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var blocked = Load();
            blocked.Add(id.ToString());
            Save(blocked);
        }
        finally { _gate.Release(); }
    }

    public async Task UnblockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var blocked = Load();
            blocked.Remove(id.ToString());
            Save(blocked);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PeerOnQId>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Load().Select(PeerOnQId.Parse).ToArray(); }
        finally { _gate.Release(); }
    }

    private HashSet<string> Load()
    {
        var json = defaults.StringForKey(StorageKey);
        return json is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<HashSet<string>>(json)
              ?? throw new InvalidDataException("The Apple blocked-device store is malformed.");
    }

    private void Save(HashSet<string> blocked) =>
        defaults.SetString(JsonSerializer.Serialize(blocked.Order(StringComparer.Ordinal)), StorageKey);
}

public sealed class AppleSessionAuditLog(NSUserDefaults defaults) : ISessionAuditLog
{
    private const string StorageKey = "peeronq.session-audit.v1";
    private const int MaximumEntries = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RecordAsync(SessionAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = Load();
            var stored = StoredAuditEntry.From(entry);
            var index = entries.FindIndex(item => item.SessionId == stored.SessionId);
            if (index >= 0) entries[index] = stored;
            else entries.Add(stored);
            entries = entries.OrderByDescending(item => item.StartedAt).Take(MaximumEntries).ToList();
            defaults.SetString(JsonSerializer.Serialize(entries), StorageKey);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SessionAuditEntry>> RecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take is <= 0 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Load()
                .OrderByDescending(item => item.StartedAt)
                .Take(take)
                .Select(item => item.ToEntry())
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    private List<StoredAuditEntry> Load()
    {
        var json = defaults.StringForKey(StorageKey);
        return json is null
            ? []
            : JsonSerializer.Deserialize<List<StoredAuditEntry>>(json)
              ?? throw new InvalidDataException("The Apple session audit is malformed.");
    }

    private sealed record StoredAuditEntry(
        string SessionId,
        SessionRole Role,
        string PeerMaskedId,
        string PeerDisplayName,
        SessionMode Mode,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        SessionEndReason EndReason)
    {
        public static StoredAuditEntry From(SessionAuditEntry entry) => new(
            entry.SessionId.ToString(),
            entry.Role,
            entry.PeerMaskedId,
            entry.PeerDisplayName,
            entry.Mode,
            entry.StartedAt,
            entry.EndedAt,
            entry.EndReason);

        public SessionAuditEntry ToEntry() => new()
        {
            SessionId = PeerOnQ.Domain.Sessions.SessionId.TryParse(SessionId, out var id)
                ? id
                : throw new InvalidDataException("Invalid Apple session audit ID."),
            Role = Role,
            PeerMaskedId = PeerMaskedId,
            PeerDisplayName = PeerDisplayName,
            Mode = Mode,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            EndReason = EndReason,
        };
    }
}

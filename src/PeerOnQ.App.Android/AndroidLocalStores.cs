using System.Text.Json;
using Android.Content;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App.Android;

public sealed class AndroidDeviceIdentityRepository(Context context) : IDeviceIdentityRepository
{
    private readonly ISharedPreferences _preferences =
        context.GetSharedPreferences("peeronq-local-state-v1", FileCreationMode.Private)
        ?? throw new InvalidOperationException("Android private preferences are unavailable.");

    public Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = _preferences.GetString("device-identity", null);
        if (json is null) return Task.FromResult<DeviceIdentity?>(null);
        var stored = JsonSerializer.Deserialize<StoredIdentity>(json)
                     ?? throw new InvalidDataException("The Android device identity is malformed.");
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
        using var editor = _preferences.Edit()
                           ?? throw new IOException("Android private preferences are unavailable.");
        editor.PutString("device-identity", JsonSerializer.Serialize(stored));
        if (!editor.Commit()) throw new IOException("Android could not persist the device identity.");
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

public sealed class AndroidBlockedDeviceStore(Context context) : IBlockedDeviceStore
{
    private readonly ISharedPreferences _preferences =
        context.GetSharedPreferences("peeronq-local-state-v1", FileCreationMode.Private)
        ?? throw new InvalidOperationException("Android private preferences are unavailable.");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> IsBlockedAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Load().Contains(id.ToString()); }
        finally { _gate.Release(); }
    }

    public async Task BlockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
        try { return Load().Select(PeerOnQId.Parse).ToArray(); }
        finally { _gate.Release(); }
    }

    private HashSet<string> Load()
    {
        var json = _preferences.GetString("blocked-devices", null);
        return json is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<HashSet<string>>(json)
              ?? throw new InvalidDataException("The Android blocked-device store is malformed.");
    }

    private void Save(HashSet<string> blocked)
    {
        using var editor = _preferences.Edit()
                           ?? throw new IOException("Android private preferences are unavailable.");
        editor.PutString("blocked-devices", JsonSerializer.Serialize(blocked.Order(StringComparer.Ordinal)));
        if (!editor.Commit()) throw new IOException("Android could not persist blocked devices.");
    }
}

public sealed class AndroidSessionAuditLog(Context context) : ISessionAuditLog
{
    private const int MaximumEntries = 100;
    private readonly ISharedPreferences _preferences =
        context.GetSharedPreferences("peeronq-local-state-v1", FileCreationMode.Private)
        ?? throw new InvalidOperationException("Android private preferences are unavailable.");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RecordAsync(SessionAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = Load();
            var stored = StoredAuditEntry.From(entry);
            var index = entries.FindIndex(item => item.SessionId == stored.SessionId);
            if (index >= 0) entries[index] = stored;
            else entries.Add(stored);
            entries = entries.OrderByDescending(item => item.StartedAt).Take(MaximumEntries).ToList();
            using var editor = _preferences.Edit()
                               ?? throw new IOException("Android private preferences are unavailable.");
            editor.PutString("session-audit", JsonSerializer.Serialize(entries));
            if (!editor.Commit()) throw new IOException("Android could not persist the session audit.");
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SessionAuditEntry>> RecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take is <= 0 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken);
        try { return Load().OrderByDescending(item => item.StartedAt).Take(take).Select(item => item.ToEntry()).ToArray(); }
        finally { _gate.Release(); }
    }

    private List<StoredAuditEntry> Load()
    {
        var json = _preferences.GetString("session-audit", null);
        return json is null
            ? []
            : JsonSerializer.Deserialize<List<StoredAuditEntry>>(json)
              ?? throw new InvalidDataException("The Android session audit is malformed.");
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
            entry.SessionId.ToString(), entry.Role, entry.PeerMaskedId, entry.PeerDisplayName,
            entry.Mode, entry.StartedAt, entry.EndedAt, entry.EndReason);

        public SessionAuditEntry ToEntry() => new()
        {
            SessionId = PeerOnQ.Domain.Sessions.SessionId.TryParse(SessionId, out var id)
                ? id
                : throw new InvalidDataException("Invalid session audit ID."),
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

using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerOnQ.Application.Collaboration;

public interface IClipboardAdapter
{
    event EventHandler? ContentChanged;
    Task<string?> ReadTextAsync(CancellationToken cancellationToken = default);
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record ClipboardSyncOptions
{
    // 120 KiB remains below the collaboration control-frame cap even when JSON must escape
    // every one-byte control character as a six-byte Unicode escape.
    public const int MaximumProtocolUtf8Bytes = 120 * 1024;
    public int MaximumUtf8Bytes { get; init; } = MaximumProtocolUtf8Bytes;
    public bool ClearRemoteContentOnSessionEnd { get; init; } = true;
}

/// <summary>
/// Plain-text-only clipboard synchronization. It starts disabled for every session and requires
/// both a signaling permission and an explicit local enable action. Content is never logged.
/// </summary>
public sealed class ClipboardSyncService : IAsyncDisposable
{
    private readonly ICollaborationTransport _transport;
    private readonly IClipboardAdapter _clipboard;
    private readonly ISecurityAuditLog? _audit;
    private readonly ClipboardSyncOptions _options;
    private readonly ILogger _log;
    private readonly Guid _originId = Guid.NewGuid();
    private readonly Lock _gate = new();
    private readonly Queue<Guid> _recentChanges = new();
    private byte[]? _lastRemoteHash;
    private byte[]? _lastSentHash;
    private bool _applyingRemote;
    private bool _paused;
    private bool _remoteEnabled;
    private int _disposed;

    public ClipboardSyncService(
        ICollaborationTransport transport,
        IClipboardAdapter clipboard,
        ClipboardSyncOptions? options = null,
        ISecurityAuditLog? audit = null,
        ILogger<ClipboardSyncService>? logger = null)
    {
        _transport = transport;
        _clipboard = clipboard;
        _options = options ?? new ClipboardSyncOptions();
        if (_options.MaximumUtf8Bytes is <= 0 or > ClipboardSyncOptions.MaximumProtocolUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Clipboard text exceeds the safe collaboration-frame bound.");
        _audit = audit;
        _log = logger ?? NullLogger<ClipboardSyncService>.Instance;
        _transport.MessageReceived += OnMessageReceived;
        _clipboard.ContentChanged += OnLocalClipboardChanged;
    }

    public bool IsEnabled { get; private set; }
    public bool IsPaused => _paused;
    public bool IsRemoteEnabled => _remoteEnabled;

    public event EventHandler<bool>? EnabledChanged;
    public event EventHandler<string>? Warning;

    public async Task EnableAsync(bool userConfirmed, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (!userConfirmed) throw new InvalidOperationException("Clipboard sharing requires explicit local confirmation.");
        if (!_transport.Permissions.HasFlag(SessionPermission.ClipboardText))
            throw new UnauthorizedAccessException("Plain-text clipboard sharing was not granted for this session.");
        if (IsEnabled) return;

        IsEnabled = true;
        _paused = false;
        EnabledChanged?.Invoke(this, true);
        await _transport.SendAsync(new ClipboardStateChange { Enabled = true }, cancellationToken);
        await AuditAsync(SecurityAuditEventType.ClipboardEnabled, "enabled");
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _paused = false;
        EnabledChanged?.Invoke(this, false);
        try { await _transport.SendAsync(new ClipboardStateChange { Enabled = false }, cancellationToken); }
        catch (InvalidOperationException) { }
        await ClearRemoteContentIfUnchangedAsync(cancellationToken);
        await AuditAsync(SecurityAuditEventType.ClipboardDisabled, "disabled");
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    private async void OnLocalClipboardChanged(object? sender, EventArgs args)
    {
        if (!IsEnabled || !_remoteEnabled || _paused || _applyingRemote) return;
        try
        {
            var text = await _clipboard.ReadTextAsync();
            if (text is null) return; // Images, files and custom formats are deliberately ignored.
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > _options.MaximumUtf8Bytes)
            {
                Warning?.Invoke(this, "Clipboard text exceeds the configured size limit.");
                await AuditAsync(SecurityAuditEventType.ClipboardRejected, "payload_too_large");
                return;
            }

            var hash = SHA256.HashData(bytes);
            lock (_gate)
            {
                if ((_lastRemoteHash is not null && CryptographicOperations.FixedTimeEquals(hash, _lastRemoteHash))
                    || (_lastSentHash is not null && CryptographicOperations.FixedTimeEquals(hash, _lastSentHash)))
                    return;
                _lastSentHash = hash;
            }

            var changeId = Guid.NewGuid();
            Remember(changeId);
            await _transport.SendAsync(new ClipboardTextUpdate
            {
                ChangeId = changeId,
                OriginId = _originId,
                Text = text,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Clipboard synchronization failed without recording clipboard content ({ErrorType})", ex.GetType().Name);
            Warning?.Invoke(this, "Clipboard synchronization failed.");
        }
    }

    private async void OnMessageReceived(object? sender, CollaborationMessage message)
    {
        if (message is ClipboardStateChange state)
        {
            _remoteEnabled = state.Enabled;
            if (!state.Enabled) await ClearRemoteContentIfUnchangedAsync(CancellationToken.None);
            return;
        }

        if (message is not ClipboardTextUpdate update || !IsEnabled || !_remoteEnabled || _paused) return;
        if (update.OriginId == _originId || WasSeen(update.ChangeId)) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(update.Text);
            if (bytes.Length > _options.MaximumUtf8Bytes)
            {
                Warning?.Invoke(this, "Remote clipboard text was rejected because it is too large.");
                await AuditAsync(SecurityAuditEventType.ClipboardRejected, "payload_too_large");
                return;
            }

            Remember(update.ChangeId);
            var hash = SHA256.HashData(bytes);
            lock (_gate) _lastRemoteHash = hash;
            _applyingRemote = true;
            try { await _clipboard.WriteTextAsync(update.Text); }
            finally { _applyingRemote = false; }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Remote clipboard update failed without recording clipboard content ({ErrorType})", ex.GetType().Name);
            Warning?.Invoke(this, "Remote clipboard update failed.");
        }
    }

    private bool WasSeen(Guid id)
    {
        lock (_gate) return _recentChanges.Contains(id);
    }

    private void Remember(Guid id)
    {
        lock (_gate)
        {
            _recentChanges.Enqueue(id);
            while (_recentChanges.Count > 128) _recentChanges.Dequeue();
        }
    }

    private async Task ClearRemoteContentIfUnchangedAsync(CancellationToken cancellationToken)
    {
        if (!_options.ClearRemoteContentOnSessionEnd || _lastRemoteHash is null) return;
        try
        {
            var current = await _clipboard.ReadTextAsync(cancellationToken);
            if (current is not null
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(current)), _lastRemoteHash))
            {
                await _clipboard.ClearAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not clear remote clipboard content at session end ({ErrorType})", ex.GetType().Name);
        }
    }

    private Task AuditAsync(SecurityAuditEventType type, string outcome) =>
        _audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = outcome,
            // Clipboard content, length and hashes are intentionally absent.
        }) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _transport.MessageReceived -= OnMessageReceived;
        _clipboard.ContentChanged -= OnLocalClipboardChanged;
        await DisableAsync();
        if (_lastRemoteHash is not null) CryptographicOperations.ZeroMemory(_lastRemoteHash);
        if (_lastSentHash is not null) CryptographicOperations.ZeroMemory(_lastSentHash);
        if (_clipboard is IDisposable disposable) disposable.Dispose();
    }
}

using PeerOnQ.Domain.Sessions;
using PeerOnQ.Application.Security;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Application.Collaboration;

public sealed class SessionCollaborationContext : IAsyncDisposable
{
    private readonly ICollaborationTransport _transport;
    private int _disposed;

    public SessionCollaborationContext(
        SessionId sessionId,
        SessionPermission permissions,
        ICollaborationTransport transport,
        FileTransferService? fileTransfers,
        ClipboardSyncService? clipboard,
        RemoteInputSession? remoteInput)
    {
        SessionId = sessionId;
        Permissions = permissions;
        _transport = transport;
        FileTransfers = fileTransfers;
        Clipboard = clipboard;
        RemoteInput = remoteInput;
        _transport.ProtocolError += OnProtocolError;
    }

    public SessionId SessionId { get; }
    public SessionPermission Permissions { get; }
    public bool IsReady => _transport.IsReady;
    public SecureSessionInfo? Security => _transport.Security;
    public FileTransferService? FileTransfers { get; }
    public ClipboardSyncService? Clipboard { get; }
    public RemoteInputSession? RemoteInput { get; }
    public TransferPriorityMode TransferPriorityMode => _transport.TransferPriorityMode;
    public bool IsNativeBulkTransportReady => _transport.IsNativeBulkTransportReady;
    public int NativeBulkBudgetKbps => _transport.NativeBulkBudgetKbps;
    public double NativeBulkGoodputKbps => _transport.NativeBulkGoodputKbps;
    public long NativeBulkFeedbackSamples => _transport.NativeBulkFeedbackSamples;

    public void SetTransferPriorityMode(TransferPriorityMode mode) =>
        _transport.SetTransferPriorityMode(mode);

    public event EventHandler? Ready
    {
        add => _transport.Ready += value;
        remove => _transport.Ready -= value;
    }

    public event EventHandler<string>? ProtocolError;

    private void OnProtocolError(object? sender, string reason)
    {
        FileTransfers?.ConnectionInterrupted();
        Clipboard?.Pause();
        RemoteInput?.ConnectionInterrupted();
        ProtocolError?.Invoke(this, reason);
    }

    public void ConnectionInterrupted()
    {
        FileTransfers?.ConnectionInterrupted();
        Clipboard?.Pause();
        RemoteInput?.ConnectionInterrupted();
    }

    public void ConnectionResumed()
    {
        _transport.ForceRekey();
        FileTransfers?.ConnectionResumed();
        Clipboard?.Resume();
        RemoteInput?.ConnectionResumed();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _transport.ProtocolError -= OnProtocolError;
        List<Exception>? failures = null;

        async ValueTask DisposePartAsync(Func<ValueTask> dispose)
        {
            try { await dispose(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }

        if (Clipboard is not null) await DisposePartAsync(Clipboard.DisposeAsync);
        if (FileTransfers is not null) await DisposePartAsync(FileTransfers.DisposeAsync);
        if (RemoteInput is not null) await DisposePartAsync(RemoteInput.DisposeAsync);
        await DisposePartAsync(_transport.DisposeAsync);

        if (failures is not null) throw new AggregateException(failures);
    }
}

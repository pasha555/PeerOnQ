using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Transport.DataPlane;

/// <summary>
/// Real reliable-channel transport over QUIC/TLS 1.3. Each logical channel owns a distinct
/// unidirectional QUIC stream; file transfer may open extra parallel lanes. System.Net.Quic does
/// not expose QUIC DATAGRAM, so this type deliberately implements only the reliable boundary.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ManagedQuicRemoteSessionTransport : IReliableRemoteSessionTransport
{
    private readonly QuicConnection _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<RemoteTransportChannel, Lazy<Task<ManagedQuicLane>>> _defaultLanes = new();
    private readonly ConcurrentDictionary<long, Task> _inboundLanes = new();
    private readonly ConcurrentDictionary<RemoteTransportChannel, byte> _exclusiveInboundLanes = new();
    private readonly IReadOnlyDictionary<RemoteTransportChannel, Channel<ReadOnlyMemory<byte>>> _incoming;
    private readonly Task _acceptLoop;
    private int _closed;
    private Exception? _terminalError;

    private ManagedQuicRemoteSessionTransport(QuicConnection connection)
    {
        _connection = connection;
        _incoming = RemoteTransportChannels.All
            .Where(definition => definition.Delivery == RemoteTransportDelivery.ReliableOrdered)
            .ToDictionary(
                definition => definition.Channel,
                definition => Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(
                    IncomingCapacity(definition.Channel))
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = !definition.AllowsParallelLanes,
                    AllowSynchronousContinuations = false,
                }));
        _acceptLoop = AcceptInboundLanesAsync();
    }

    public static bool IsSupported => ManagedQuicSessionListener.IsSupported;

    public EndPoint LocalEndPoint => _connection.LocalEndPoint;
    public EndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public RemoteTransportCapabilities Capabilities { get; } = new()
    {
        Kind = RemoteSessionTransportKind.NativeQuic,
        ProtocolVersion = PeerOnQQuicProtocol.Version,
        SupportsReliableStreams = true,
        SupportsUnreliableDatagrams = false,
        SupportsPathMigration = false,
    };

    public static async ValueTask<ManagedQuicRemoteSessionTransport> ConnectAsync(
        IPEndPoint remoteEndPoint,
        X509Certificate2 localCertificate,
        string expectedPeerCertificateSha256,
        IPEndPoint? localEndPoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ManagedQuicSessionListener.EnsureSupported();
        QuicPeerAuthentication.ValidateLocalCertificate(localCertificate);
        var peerAuthentication = new QuicPeerAuthentication(expectedPeerCertificateSha256);
        var certificates = new X509CertificateCollection { localCertificate };
        var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndPoint,
            LocalEndPoint = localEndPoint,
            DefaultCloseErrorCode = PeerOnQQuicProtocol.ProtocolErrorCode,
            DefaultStreamErrorCode = PeerOnQQuicProtocol.ProtocolErrorCode,
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            IdleTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 32,
            InitialReceiveWindowSizes = ManagedQuicSessionListener.CreateReceiveWindows(),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [PeerOnQQuicProtocol.ApplicationProtocol],
                EnabledSslProtocols = SslProtocols.Tls13,
                TargetHost = "peeronq-session",
                ClientCertificates = certificates,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = peerAuthentication.Validate,
            },
        }, cancellationToken);

        if (connection.NegotiatedApplicationProtocol != PeerOnQQuicProtocol.ApplicationProtocol)
        {
            await connection.DisposeAsync();
            throw new AuthenticationException("The peer negotiated an unsupported data-plane protocol.");
        }

        return Attach(connection);
    }

    internal static ManagedQuicRemoteSessionTransport Attach(QuicConnection connection) => new(connection);

    public async ValueTask SendAsync(
        RemoteTransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var definition = RequireReliableChannel(channel);
        EnsurePayloadSize(definition, payload.Length);
        var lane = await _defaultLanes.GetOrAdd(
            channel,
            key => new Lazy<Task<ManagedQuicLane>>(
                () => CreateLaneAsync(key, parallelLane: false, _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value.WaitAsync(cancellationToken);
        await lane.SendAsync(payload, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        RemoteTransportChannel channel,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireReliableChannel(channel);
        if (!_incoming.TryGetValue(channel, out var queue))
            throw new ArgumentOutOfRangeException(nameof(channel));

        await foreach (var message in queue.Reader.ReadAllAsync(cancellationToken))
            yield return message;
    }

    public async ValueTask<IRemoteTransportLane> OpenLaneAsync(
        RemoteTransportChannel channel,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var definition = RequireReliableChannel(channel);
        if (!definition.AllowsParallelLanes)
            throw new InvalidOperationException($"Channel '{channel}' has one ordered lane per direction.");
        return await CreateLaneAsync(channel, parallelLane: true, cancellationToken);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            await _connection.CloseAsync(PeerOnQQuicProtocol.ShutdownErrorCode, cancellationToken);
        }
        finally
        {
            _lifetime.Cancel();
            CompleteIncoming();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync();
        }
        catch (Exception ex) when (ex is QuicException or OperationCanceledException)
        {
            // The peer may already have closed; disposal still owns all local resources.
        }

        try { await _acceptLoop; }
        catch (Exception) { /* surfaced through the per-channel readers */ }

        var laneTasks = _defaultLanes.Values
            .Where(lazy => lazy.IsValueCreated)
            .Select(lazy => lazy.Value)
            .ToArray();
        foreach (var task in laneTasks)
        {
            if (!task.IsCompletedSuccessfully) continue;
            await task.Result.DisposeAsync();
        }

        await _connection.DisposeAsync();
        _lifetime.Dispose();
    }

    private async Task<ManagedQuicLane> CreateLaneAsync(
        RemoteTransportChannel channel,
        bool parallelLane,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        try
        {
            var header = new byte[PeerOnQQuicProtocol.StreamHeaderLength];
            PeerOnQQuicProtocol.WriteStreamHeader(header, channel, parallelLane);
            await stream.WriteAsync(header, completeWrites: false, cancellationToken);
            return new ManagedQuicLane(stream, RemoteTransportChannels.Get(channel));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task AcceptInboundLanesAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(_lifetime.Token);
                var task = ProcessInboundLaneAsync(stream);
                _inboundLanes[stream.Id] = task;
                _ = task.ContinueWith(
                    (completedTask, state) =>
                    {
                        _ = completedTask.Exception;
                        var tuple = ((ConcurrentDictionary<long, Task> Lanes, long Id))state!;
                        tuple.Lanes.TryRemove(tuple.Id, out _);
                    },
                    (_inboundLanes, stream.Id),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Local close.
        }
        catch (QuicException ex) when (Volatile.Read(ref _closed) != 0 || IsNormalPeerClose(ex))
        {
            Interlocked.Exchange(ref _closed, 1);
            _lifetime.Cancel();
            CompleteIncoming();
        }
        catch (Exception ex)
        {
            await TerminateAsync(ex);
        }
    }

    private async Task ProcessInboundLaneAsync(QuicStream stream)
    {
        RemoteTransportChannel? exclusiveChannel = null;
        await using (stream)
        {
            try
            {
                var header = new byte[PeerOnQQuicProtocol.StreamHeaderLength];
                await stream.ReadExactlyAsync(header, _lifetime.Token);
                if (!PeerOnQQuicProtocol.TryReadStreamHeader(header, out var channel, out var parallelLane))
                    throw new InvalidDataException("The peer sent an invalid QUIC stream header.");

                var definition = RequireReliableChannel(channel);
                if (!parallelLane && !_exclusiveInboundLanes.TryAdd(channel, 0))
                    throw new InvalidDataException($"The peer opened a duplicate ordered '{channel}' lane.");
                if (!parallelLane) exclusiveChannel = channel;

                var lengthBuffer = new byte[PeerOnQQuicProtocol.MessageLengthPrefixLength];
                while (await TryReadExactlyAsync(stream, lengthBuffer, _lifetime.Token))
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
                    EnsurePayloadSize(definition, length);
                    var payload = GC.AllocateUninitializedArray<byte>(length);
                    if (length > 0) await stream.ReadExactlyAsync(payload, _lifetime.Token);
                    await _incoming[channel].Writer.WriteAsync(payload, _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Connection teardown.
            }
            catch (Exception ex)
            {
                await TerminateAsync(ex);
            }
            finally
            {
                if (exclusiveChannel is { } channel)
                    _exclusiveInboundLanes.TryRemove(channel, out _);
            }
        }
    }

    private async Task TerminateAsync(Exception error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _terminalError = error;
        _lifetime.Cancel();
        CompleteIncoming();
        try
        {
            await _connection.CloseAsync(PeerOnQQuicProtocol.ProtocolErrorCode, CancellationToken.None);
        }
        catch (Exception ex) when (ex is QuicException or OperationCanceledException)
        {
            // The original protocol error is the useful failure.
        }
    }

    private void CompleteIncoming()
    {
        foreach (var queue in _incoming.Values)
            queue.Writer.TryComplete(_terminalError);
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
    }

    private static RemoteTransportChannelDefinition RequireReliableChannel(RemoteTransportChannel channel)
    {
        var definition = RemoteTransportChannels.Get(channel);
        if (definition.Delivery != RemoteTransportDelivery.ReliableOrdered)
            throw new ArgumentException($"Channel '{channel}' requires QUIC DATAGRAM.", nameof(channel));
        return definition;
    }

    private static void EnsurePayloadSize(RemoteTransportChannelDefinition definition, int length)
    {
        if (length < 0 || length > definition.MaximumMessageBytes)
            throw new InvalidDataException(
                $"A '{definition.Channel}' message must be between 0 and {definition.MaximumMessageBytes} bytes.");
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("The peer closed a QUIC stream inside a message header.");
            }
            offset += read;
        }
        return true;
    }

    private static int IncomingCapacity(RemoteTransportChannel channel) => channel switch
    {
        RemoteTransportChannel.Mouse or RemoteTransportChannel.Keyboard => 256,
        RemoteTransportChannel.Control => 128,
        RemoteTransportChannel.Clipboard => 8,
        RemoteTransportChannel.FileTransfer => 16,
        _ => 32,
    };

    private static bool IsNormalPeerClose(QuicException exception) =>
        exception.QuicError is QuicError.ConnectionAborted or QuicError.OperationAborted
        && exception.ApplicationErrorCode == PeerOnQQuicProtocol.ShutdownErrorCode;

    private sealed class ManagedQuicLane(
        QuicStream stream,
        RemoteTransportChannelDefinition definition) : IRemoteTransportLane
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private int _disposed;

        public RemoteTransportChannel Channel => definition.Channel;

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            EnsurePayloadSize(definition, payload.Length);
            await _sendGate.WaitAsync(cancellationToken);
            byte[]? rented = null;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                rented = ArrayPool<byte>.Shared.Rent(
                    PeerOnQQuicProtocol.MessageLengthPrefixLength + payload.Length);
                var frame = rented.AsMemory(0, PeerOnQQuicProtocol.MessageLengthPrefixLength + payload.Length);
                BinaryPrimitives.WriteInt32BigEndian(frame.Span, payload.Length);
                payload.CopyTo(frame[PeerOnQQuicProtocol.MessageLengthPrefixLength..]);
                await stream.WriteAsync(frame, completeWrites: false, cancellationToken);
            }
            finally
            {
                if (rented is not null)
                {
                    CryptographicOperations.ZeroMemory(
                        rented.AsSpan(0, PeerOnQQuicProtocol.MessageLengthPrefixLength + payload.Length));
                    ArrayPool<byte>.Shared.Return(rented);
                }
                _sendGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _sendGate.WaitAsync();
            try
            {
                stream.CompleteWrites();
                await stream.DisposeAsync();
            }
            finally
            {
                _sendGate.Release();
                _sendGate.Dispose();
            }
        }
    }
}

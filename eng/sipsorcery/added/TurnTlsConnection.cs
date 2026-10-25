//-----------------------------------------------------------------------------
// Filename: TurnTlsConnection.cs
//
// Description: TLS-over-TCP transport to TURN servers ("turns:" URIs) for the
// RTP ICE channel, and the framing of TURN messages on stream transports.
//
// - RFC 7065: TURN URIs, "turns" without a transport parameter is TLS over TCP.
// - RFC 8656 section 3.1 (RFC 5766 section 2.1) and RFC 7350: TURN over
//   TLS-over-TCP, default port 5349, the server authenticated by certificate.
// - RFC 8489 section 6.2.2 (RFC 5389 section 7.2.2): STUN over stream
//   transports, messages are delimited by their length field.
// - RFC 8656 section 12.5 (RFC 5766 section 11.5): ChannelData messages are
//   padded to a multiple of four bytes over TCP and TLS-over-TCP.
//
// Author(s):
// PeerOnQ maintainers
//
// History:
// 25 Oct 2026	Created for TURN over TLS in the RTP ICE channel.
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.Net
{
    /// <summary>
    /// The outcome of trying to take the next message off a TURN stream.
    /// </summary>
    internal enum TurnStreamFrameResult
    {
        /// <summary>The buffered bytes do not hold a complete message yet.</summary>
        NeedMoreData,

        /// <summary>A complete STUN message was extracted.</summary>
        Stun,

        /// <summary>A complete ChannelData message was extracted (without its padding).</summary>
        ChannelData,

        /// <summary>The buffered bytes cannot start a TURN message: the stream framing is lost.</summary>
        Invalid
    }

    /// <summary>
    /// Splits the byte stream of a TURN connection over TCP or TLS-over-TCP into messages. Both message
    /// types carry their length: a STUN message (first two bits 00) is the 20 byte header plus the
    /// 16-bit message length, which is always a multiple of 4; a ChannelData message (first two bits 01)
    /// is a 4 byte header plus the 16-bit data length, padded to a multiple of 4 on stream transports.
    /// Any other start means the framing of the stream has been lost and cannot be recovered.
    /// </summary>
    internal sealed class TurnStreamFramer
    {
        private const int CHANNEL_DATA_HEADER_LENGTH = 4;
        private const int STUN_FIXED_PREFIX_LENGTH = 8; // Type, length and magic cookie.

        /// <summary>
        /// The longest message either type can declare: a STUN message of 20 + 65532 bytes (a ChannelData
        /// message is at most 4 + 65535 + 1 padding bytes).
        /// </summary>
        internal const int MAX_FRAME_LENGTH = STUNHeader.STUN_HEADER_LENGTH + 0xFFFC;

        private readonly byte[] _buffer = new byte[MAX_FRAME_LENGTH];
        private int _start;
        private int _end;

        /// <summary>
        /// The number of received bytes that have not been returned as a message yet.
        /// </summary>
        internal int BufferedCount => _end - _start;

        /// <summary>
        /// Gets the free space after the buffered bytes, moving any partial message to the start of the
        /// buffer first. Because the buffer holds the longest possible message, the segment is never empty
        /// while all complete messages have been taken off.
        /// </summary>
        internal ArraySegment<byte> GetReceiveSegment()
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            return new ArraySegment<byte>(_buffer, _end, _buffer.Length - _end);
        }

        /// <summary>
        /// Records that <paramref name="count"/> bytes were written into the last receive segment.
        /// </summary>
        internal void Commit(int count)
        {
            if (count < 0 || count > _buffer.Length - _end)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _end += count;
        }

        /// <summary>
        /// Copies received bytes into the buffer. Returns how many bytes fitted.
        /// </summary>
        internal int Append(byte[] data, int offset, int count)
        {
            var segment = GetReceiveSegment();
            int copied = Math.Min(count, segment.Count);
            Buffer.BlockCopy(data, offset, segment.Array, segment.Offset, copied);
            _end += copied;
            return copied;
        }

        /// <summary>
        /// Takes the next complete message off the buffer.
        /// </summary>
        /// <param name="message">The STUN message, or the ChannelData message without padding.</param>
        /// <param name="error">Why the framing is lost when the result is <see cref="TurnStreamFrameResult.Invalid"/>.</param>
        internal TurnStreamFrameResult TryReadFrame(out byte[] message, out string error)
        {
            message = null;
            error = null;

            int available = _end - _start;
            if (available < CHANNEL_DATA_HEADER_LENGTH)
            {
                return TurnStreamFrameResult.NeedMoreData;
            }

            int firstByte = _buffer[_start];
            int length = (_buffer[_start + 2] << 8) | _buffer[_start + 3];
            int messageLength;
            int paddedLength;
            TurnStreamFrameResult result;

            switch (firstByte >> 6)
            {
                case 0:
                    if ((length & 0x03) != 0)
                    {
                        error = $"STUN message length {length} is not a multiple of 4";
                        return TurnStreamFrameResult.Invalid;
                    }

                    if (available < STUN_FIXED_PREFIX_LENGTH)
                    {
                        return TurnStreamFrameResult.NeedMoreData;
                    }

                    uint cookie = (uint)((_buffer[_start + 4] << 24) | (_buffer[_start + 5] << 16) | (_buffer[_start + 6] << 8) | _buffer[_start + 7]);
                    if (cookie != STUNHeader.MAGIC_COOKIE)
                    {
                        error = "STUN message without the magic cookie";
                        return TurnStreamFrameResult.Invalid;
                    }

                    messageLength = STUNHeader.STUN_HEADER_LENGTH + length;
                    paddedLength = messageLength;
                    result = TurnStreamFrameResult.Stun;
                    break;

                case 1:
                    messageLength = CHANNEL_DATA_HEADER_LENGTH + length;
                    paddedLength = (messageLength + 3) & ~3;
                    result = TurnStreamFrameResult.ChannelData;
                    break;

                default:
                    error = $"first byte 0x{firstByte:X2} starts neither a STUN nor a ChannelData message";
                    return TurnStreamFrameResult.Invalid;
            }

            if (available < paddedLength)
            {
                return TurnStreamFrameResult.NeedMoreData;
            }

            message = new byte[messageLength];
            Buffer.BlockCopy(_buffer, _start, message, 0, messageLength);
            _start += paddedLength;

            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            return result;
        }
    }

    /// <summary>
    /// A TLS-over-TCP connection from an RTP ICE channel to a TURN server ("turns:" URI). It uses the
    /// TCP socket the channel created for the URI (and lists in <see cref="RtpIceChannel.RtpTcpSocketByUri"/>),
    /// so closing that socket tears the connection down. The server certificate is validated normally
    /// against the URI host unless the application supplied a validation callback. All TURN requests,
    /// indications and relayed data for the server go over this connection; received STUN messages are
    /// handed to the channel one at a time. Writes are serialised through a bounded queue.
    /// </summary>
    internal sealed class TurnTlsConnection
    {
        internal enum ConnectionState
        {
            New = 0,
            Connecting = 1,
            Connected = 2,
            Closed = 3
        }

        private static readonly ILogger logger = Log.Logger;

#if NETCOREAPP3_0_OR_GREATER
        private const SslProtocols TLS_PROTOCOLS = SslProtocols.Tls12 | SslProtocols.Tls13;
#else
        private const SslProtocols TLS_PROTOCOLS = SslProtocols.Tls12;
#endif

        /// <summary>
        /// Queued messages are coalesced into writes of up to one TLS record.
        /// </summary>
        private const int SEND_BATCH_SIZE = 16 * 1024;

        private const int DROPPED_SEND_LOG_INTERVAL_SECONDS = 5;

        private readonly Socket _socket;
        private readonly string _targetHost;
        private readonly Func<RemoteCertificateValidationCallback> _getValidationCallback;
        private readonly Action<TurnTlsConnection, byte[]> _onStunMessage;
        private readonly int _maxQueuedBytes;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly byte[] _sendBatch = new byte[SEND_BATCH_SIZE];

        private SslStream _sslStream;
        private int _state = (int)ConnectionState.New;
        private int _error = (int)SocketError.Success;
        private int _queuedBytes;
        private int _sending;
        private long _droppedSends;
        private long _droppedSendsLogTicks;
        private int _channelDataWarned;

        /// <param name="uri">The "turns:" URI the connection is for.</param>
        /// <param name="socket">The bound, unconnected TCP socket to connect.</param>
        /// <param name="getValidationCallback">Returns the application's certificate validation callback
        /// or null for normal validation. Called when the server certificate arrives.</param>
        /// <param name="onStunMessage">Receives each STUN message read from the server.</param>
        /// <param name="maxQueuedBytes">The most bytes that may wait to be written.</param>
        internal TurnTlsConnection(STUNUri uri, Socket socket, Func<RemoteCertificateValidationCallback> getValidationCallback,
            Action<TurnTlsConnection, byte[]> onStunMessage, int maxQueuedBytes)
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _targetHost = GetTargetHost(uri);
            _getValidationCallback = getValidationCallback;
            _onStunMessage = onStunMessage ?? throw new ArgumentNullException(nameof(onStunMessage));
            _maxQueuedBytes = Math.Max(TurnStreamFramer.MAX_FRAME_LENGTH, maxQueuedBytes);
            LocalPort = (socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;
        }

        internal STUNUri Uri { get; }

        internal ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

        /// <summary>
        /// Why the connection failed, or Success if it has not failed.
        /// </summary>
        internal SocketError Error => (SocketError)Volatile.Read(ref _error);

        /// <summary>
        /// The TURN server end point the connection was opened to.
        /// </summary>
        internal IPEndPoint ServerEndPoint { get; private set; }

        internal int LocalPort { get; }

        /// <summary>
        /// The host name (or IP address) the server certificate must match: the host of the URI.
        /// </summary>
        internal static string GetTargetHost(STUNUri uri)
        {
            string host = uri?.Host?.Trim() ?? string.Empty;
            return host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']' ? host.Substring(1, host.Length - 2) : host;
        }

        /// <summary>
        /// Starts connecting in the background. Only the first call on a new connection does anything.
        /// </summary>
        internal bool Connect(IPEndPoint serverEndPoint, TimeSpan timeout)
        {
            if (serverEndPoint == null)
            {
                throw new ArgumentNullException(nameof(serverEndPoint));
            }

            if (Interlocked.CompareExchange(ref _state, (int)ConnectionState.Connecting, (int)ConnectionState.New) != (int)ConnectionState.New)
            {
                return false;
            }

            ServerEndPoint = serverEndPoint;
            _ = ConnectAndReceiveAsync(serverEndPoint, timeout);
            return true;
        }

        /// <summary>
        /// Queues a STUN message for the server. Returns NotConnected unless the connection is up. When
        /// the server cannot keep up and the queue is full the message is dropped (like a datagram, which
        /// the relayed media is) and a warning is logged at most every few seconds.
        /// </summary>
        internal SocketError Send(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return SocketError.InvalidArgument;
            }
            else if (State != ConnectionState.Connected)
            {
                return SocketError.NotConnected;
            }

            if (Interlocked.Add(ref _queuedBytes, buffer.Length) > _maxQueuedBytes)
            {
                Interlocked.Add(ref _queuedBytes, -buffer.Length);
                LogDroppedSend();
                return SocketError.Success;
            }

            _sendQueue.Enqueue(buffer);

            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            {
                _ = Task.Run(SendQueuedAsync);
            }

            return SocketError.Success;
        }

        /// <summary>
        /// Closes the connection and its socket. Safe to call more than once and from any thread.
        /// </summary>
        internal void Close(string reason)
        {
            if (Interlocked.Exchange(ref _state, (int)ConnectionState.Closed) == (int)ConnectionState.Closed)
            {
                return;
            }

            logger.LogDebug("TLS connection to TURN server {Uri} closed: {Reason}.", Uri, reason);

            try
            {
                if (_socket.Connected)
                {
                    _socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (SocketException)
            {
                // Already reset by the server.
            }
            catch (ObjectDisposedException)
            {
                // Already closed, for example by the application.
            }

            // Disposing the TLS stream disposes the network stream that owns the socket; the socket is
            // closed directly as well in case the TLS stream was never created.
            Volatile.Read(ref _sslStream)?.Dispose();
            _socket.Close();

            while (_sendQueue.TryDequeue(out _))
            { }
            Interlocked.Exchange(ref _queuedBytes, 0);
        }

        private void Fail(SocketError error, string reason, Exception excp = null)
        {
            if (State == ConnectionState.Closed)
            {
                return;
            }

            Interlocked.CompareExchange(ref _error, (int)error, (int)SocketError.Success);

            // Network and TLS failures are expected and their message says enough; anything else is logged with its stack.
            bool expected = excp == null || excp is IOException || excp is SocketException || excp is AuthenticationException || excp is ObjectDisposedException;
            logger.LogWarning(expected ? null : excp, "TLS connection to TURN server {Uri} failed ({SocketError}): {Reason}", Uri, error, reason);
            Close(reason);
        }

        private async Task ConnectAndReceiveAsync(IPEndPoint serverEndPoint, TimeSpan timeout)
        {
            SslStream sslStream;
            SslProtocols sslProtocol;

            try
            {
                using (var timeoutSource = new CancellationTokenSource(timeout))
                using (timeoutSource.Token.Register(() => Fail(SocketError.TimedOut, $"not connected within {timeout.TotalSeconds:0.#}s")))
                {
                    var connectEndPoint = serverEndPoint.AddressFamily == AddressFamily.InterNetwork && _socket.AddressFamily == AddressFamily.InterNetworkV6 ?
                        new IPEndPoint(serverEndPoint.Address.MapToIPv6(), serverEndPoint.Port) :
                        serverEndPoint;

                    await _socket.ConnectAsync(connectEndPoint).ConfigureAwait(false);

                    sslStream = new SslStream(new NetworkStream(_socket, true), false, ValidateServerCertificate);
                    Volatile.Write(ref _sslStream, sslStream);

                    if (State == ConnectionState.Closed)
                    {
                        // Closed while connecting, before Close could see the TLS stream.
                        sslStream.Dispose();
                        return;
                    }

                    await sslStream.AuthenticateAsClientAsync(_targetHost, null, TLS_PROTOCOLS, false).ConfigureAwait(false);
                    sslProtocol = sslStream.SslProtocol;
                }
            }
            catch (Exception excp)
            {
                Fail(GetSocketError(excp), excp is AuthenticationException ? $"TLS handshake failed: {excp.Message}" : excp.Message, excp);
                return;
            }

            if (Interlocked.CompareExchange(ref _state, (int)ConnectionState.Connected, (int)ConnectionState.Connecting) != (int)ConnectionState.Connecting)
            {
                // Closed (or timed out) at the end of the handshake.
                return;
            }

            logger.LogDebug("TLS connection to TURN server {Uri} established from local port {LocalPort} to {ServerEndPoint} ({SslProtocol}).",
                Uri, LocalPort, serverEndPoint, sslProtocol);

            await ReceiveAsync(sslStream).ConfigureAwait(false);
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            var callback = _getValidationCallback?.Invoke();

            if (callback == null)
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    return true;
                }

                logger.LogWarning("TURN server {Uri} presented a certificate that is not valid for {TargetHost} ({SslPolicyErrors}, subject {Subject}).",
                    Uri, _targetHost, sslPolicyErrors, certificate?.Subject);
                return false;
            }

            try
            {
                if (callback(sender, certificate, chain, sslPolicyErrors))
                {
                    return true;
                }

                logger.LogWarning("The certificate validation callback rejected the certificate of TURN server {Uri} ({SslPolicyErrors}, subject {Subject}).",
                    Uri, sslPolicyErrors, certificate?.Subject);
                return false;
            }
            catch (Exception excp)
            {
                logger.LogWarning(excp, "The certificate validation callback for TURN server {Uri} failed, rejecting the certificate. {ErrorMessage}", Uri, excp.Message);
                return false;
            }
        }

        private async Task ReceiveAsync(SslStream sslStream)
        {
            var framer = new TurnStreamFramer();

            try
            {
                while (State == ConnectionState.Connected)
                {
                    var segment = framer.GetReceiveSegment();
                    int bytesRead = await sslStream.ReadAsync(segment.Array, segment.Offset, segment.Count).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        Fail(SocketError.ConnectionReset, "closed by the TURN server");
                        return;
                    }

                    framer.Commit(bytesRead);

                    if (!DispatchMessages(framer))
                    {
                        return;
                    }
                }
            }
            catch (Exception excp)
            {
                // After a local close the pending read ends with an exception; that is expected.
                Fail(GetSocketError(excp), $"receive failed: {excp.Message}", excp);
            }
        }

        /// <summary>
        /// Hands every complete message to the channel. A message that cannot be processed is logged
        /// and dropped; only lost framing ends the connection. Returns false when the connection closed.
        /// </summary>
        private bool DispatchMessages(TurnStreamFramer framer)
        {
            while (State == ConnectionState.Connected)
            {
                switch (framer.TryReadFrame(out var message, out var error))
                {
                    case TurnStreamFrameResult.NeedMoreData:
                        return true;

                    case TurnStreamFrameResult.Invalid:
                        Fail(SocketError.ProtocolNotSupported, $"stream framing lost, {error}");
                        return false;

                    case TurnStreamFrameResult.ChannelData:
                        // This channel never binds channels (relayed data uses Send and Data indications),
                        // so there is no peer for the channel number.
                        if (Interlocked.Exchange(ref _channelDataWarned, 1) == 0)
                        {
                            logger.LogWarning("TURN server {Uri} sent ChannelData for an unbound channel, dropping it.", Uri);
                        }
                        break;

                    default:
                        try
                        {
                            _onStunMessage(this, message);
                        }
                        catch (Exception excp)
                        {
                            logger.LogWarning(excp, "Dropped a message from TURN server {Uri} that could not be processed. {ErrorMessage}", Uri, excp.Message);
                        }
                        break;
                }
            }

            return false;
        }

        private async Task SendQueuedAsync()
        {
            try
            {
                while (true)
                {
                    var sslStream = Volatile.Read(ref _sslStream);

                    while (State == ConnectionState.Connected && _sendQueue.TryDequeue(out var message))
                    {
                        Interlocked.Add(ref _queuedBytes, -message.Length);

                        if (message.Length >= SEND_BATCH_SIZE)
                        {
                            await sslStream.WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                            continue;
                        }

                        // Only this method dequeues and it runs once at a time, so the peeked message is
                        // the one dequeued next.
                        Buffer.BlockCopy(message, 0, _sendBatch, 0, message.Length);
                        int batchLength = message.Length;

                        while (_sendQueue.TryPeek(out var next) && batchLength + next.Length <= SEND_BATCH_SIZE && _sendQueue.TryDequeue(out next))
                        {
                            Interlocked.Add(ref _queuedBytes, -next.Length);
                            Buffer.BlockCopy(next, 0, _sendBatch, batchLength, next.Length);
                            batchLength += next.Length;
                        }

                        await sslStream.WriteAsync(_sendBatch, 0, batchLength).ConfigureAwait(false);
                    }

                    Volatile.Write(ref _sending, 0);

                    if (State != ConnectionState.Connected || _sendQueue.IsEmpty || Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
                    {
                        return;
                    }
                }
            }
            catch (Exception excp)
            {
                Volatile.Write(ref _sending, 0);
                Fail(GetSocketError(excp), $"send failed: {excp.Message}", excp);
            }
        }

        private void LogDroppedSend()
        {
            long dropped = Interlocked.Increment(ref _droppedSends);
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _droppedSendsLogTicks);

            if (now - last >= TimeSpan.TicksPerSecond * DROPPED_SEND_LOG_INTERVAL_SECONDS &&
                Interlocked.CompareExchange(ref _droppedSendsLogTicks, now, last) == last)
            {
                logger.LogWarning("TLS connection to TURN server {Uri} cannot keep up, {Dropped} message(s) dropped so far ({QueuedBytes} bytes queued).",
                    Uri, dropped, Volatile.Read(ref _queuedBytes));
            }
        }

        private static SocketError GetSocketError(Exception excp)
        {
            switch (excp)
            {
                case SocketException sockExcp:
                    return sockExcp.SocketErrorCode;
                case IOException ioExcp when ioExcp.InnerException is SocketException innerSockExcp:
                    return innerSockExcp.SocketErrorCode;
                case AuthenticationException _:
                    return SocketError.AccessDenied;
                case ObjectDisposedException _:
                    return SocketError.OperationAborted;
                case IOException _:
                    return SocketError.ConnectionAborted;
                default:
                    return SocketError.SocketError;
            }
        }
    }
}

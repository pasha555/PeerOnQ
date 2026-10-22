using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class UdpReceiveAdmissionTests
{
    [Fact]
    public async Task Concurrent_restarts_keep_one_callback_owner_and_deliver_valid_datagrams_once()
    {
        const int packetCount = 128;
        using var socket = BoundSocket();
        using var sender = BoundSocket();
        var receiver = new UdpReceiver(socket);
        using var stop = new CancellationTokenSource();
        using var start = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();
        var received = new ConcurrentDictionary<int, byte>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0, maximumCallbacks = 0, invalidPackets = 0, duplicatePackets = 0;
        bool allWorkersJoined = true;
        receiver.OnPacketReceived += (_, _, _, packet) =>
        {
            int active = Interlocked.Increment(ref callbacks);
            int previous;
            do
            {
                previous = Volatile.Read(ref maximumCallbacks);
                if (active <= previous) break;
            } while (Interlocked.CompareExchange(ref maximumCallbacks, active, previous) != previous);
            try
            {
                int id = packet.Length == 64 ? BinaryPrimitives.ReadInt32LittleEndian(packet) : -1;
                if (id < 0 || id >= packetCount ||
                    !packet.AsSpan(4).SequenceEqual(Packet(id).AsSpan(4)))
                    Interlocked.Increment(ref invalidPackets);
                else if (!received.TryAdd(id, 0))
                    Interlocked.Increment(ref duplicatePackets);
                // Keep the callback active while ordinary restart callers race with receive completion.
                Thread.Sleep(1);
                if (received.Count == packetCount) completed.TrySetResult();
            }
            finally { Interlocked.Decrement(ref callbacks); }
        };
        var workers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            start.Wait();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    receiver.BeginReceiveFrom();
                    Thread.Yield();
                }
            }
            catch (Exception error) { errors.Enqueue(error); }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var worker in workers) worker.Start();
            start.Set();
            var destination = socket.LocalEndPoint!;
            for (int id = 0; id < packetCount; id++)
                Assert.Equal(64, sender.SendTo(Packet(id), destination));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            start.Set();
            receiver.Close("test completed");
            foreach (var worker in workers) allWorkersJoined &= worker.Join(TimeSpan.FromSeconds(2));
        }
        Assert.True(allWorkersJoined);
        Assert.Empty(errors);
        Assert.Equal(packetCount, received.Count);
        Assert.Equal(0, invalidPackets);
        Assert.Equal(0, duplicatePackets);
        Assert.Equal(1, maximumCallbacks);
        Assert.True(SpinWait.SpinUntil(() => !receiver.IsRunningReceive, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Close_during_restarts_releases_the_receiver_and_prevents_rearming()
    {
        using var socket = BoundSocket();
        var receiver = new UdpReceiver(socket);
        var errors = new ConcurrentQueue<Exception>();
        var worker = new Thread(() =>
        {
            try { for (int i = 0; i < 1000; i++) receiver.BeginReceiveFrom(); }
            catch (Exception error) { errors.Enqueue(error); }
        }) { IsBackground = true };
        receiver.BeginReceiveFrom();
        worker.Start();
        receiver.Close("test close race");
        Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
        Assert.Empty(errors);
        Assert.True(receiver.IsClosed);
        Assert.True(SpinWait.SpinUntil(() => !receiver.IsRunningReceive, TimeSpan.FromSeconds(2)));
        receiver.BeginReceiveFrom();
        Assert.False(receiver.IsRunningReceive);
    }

    [Fact]
    public void Failed_native_post_releases_receive_admission()
    {
        using var socket = BoundSocket();
        var receiver = new UdpReceiver(socket);
        socket.Dispose();
        for (int i = 0; i < 8; i++)
        {
            receiver.BeginReceiveFrom();
            Assert.False(receiver.IsRunningReceive);
        }
        receiver.Close("test disposed socket");
        Assert.True(receiver.IsClosed);
    }

    private static Socket BoundSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static byte[] Packet(int id)
    {
        var packet = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(packet, id);
        for (int i = 4; i < packet.Length; i++) packet[i] = unchecked((byte)(id * 31 + i));
        return packet;
    }
}

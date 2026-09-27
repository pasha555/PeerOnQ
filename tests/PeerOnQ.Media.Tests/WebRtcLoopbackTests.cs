using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using SIPSorcery.Net;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.Media.Tests;

public sealed class LiveTurnFactAttribute : FactAttribute
{
    public LiveTurnFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PEERONQ_LIVE_TURN_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set PEERONQ_LIVE_TURN_TEST=1 through the local Phase 3 acceptance script.";
        }
    }
}

[CollectionDefinition("Real WebRTC acceptance", DisableParallelization = true)]
public sealed class RealWebRtcAcceptanceCollection;

/// <summary>
/// Synthetic screen: emits real I420 frames so the VP8 encoder has something to compress.
/// </summary>
public sealed class SyntheticCaptureSource(int width = 320, int height = 240) : IScreenCaptureSource
{
    private readonly ConcurrentDictionary<long, long> _captureTimestamps = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _sequence;

    public bool IsCapturing { get; private set; }
    public CaptureTargetInfo? Target { get; private set; }

    public event EventHandler<CapturedFrame>? FrameArrived;
    public event EventHandler<CaptureStoppedReason>? CaptureStopped;

    public Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        Target = request.Target;
        IsCapturing = true;
        _cts = new CancellationTokenSource();

        _loop = Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                FrameArrived?.Invoke(this, NextFrame());
                await Task.Delay(33, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private CapturedFrame NextFrame()
    {
        var ySize = width * height;
        var buffer = new byte[ySize * 3 / 2];
        var shade = (byte)(_sequence % 200 + 30);

        // Moving gradient in luma, neutral chroma.
        for (var i = 0; i < ySize; i++)
        {
            buffer[i] = (byte)((i + shade) % 256);
        }

        Array.Fill(buffer, (byte)128, ySize, buffer.Length - ySize);

        var sequence = Interlocked.Increment(ref _sequence);
        _captureTimestamps[sequence] = Stopwatch.GetTimestamp();
        return new CapturedFrame
        {
            Width = width,
            Height = height,
            I420 = buffer,
            Timestamp = TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency),
            SequenceNumber = sequence,
        };
    }

    public bool TryGetCaptureTimestamp(long sequence, out long timestamp) =>
        _captureTimestamps.TryGetValue(sequence, out timestamp);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        _cts?.Cancel();
        CaptureStopped?.Invoke(this, CaptureStoppedReason.StoppedByUser);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

[Collection("Real WebRTC acceptance")]
public class WebRtcLoopbackTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(128, 1_024, 80)]
    [InlineData(100_000, 62_500, 62_500)]
    [InlineData(150_000, 65_536, 93_750)]
    public void Bulk_fragment_and_queue_budgets_bound_sender_head_of_line_delay(
        int maximumBulkKbps,
        int expectedFragmentBytes,
        ulong expectedQueueBytes)
    {
        var allocation = new TransferAllocationPolicy(
            MaximumBulkBufferedBytes: 2UL * 1024 * 1024,
            MaximumBulkKbps: maximumBulkKbps);

        Assert.Equal(
            expectedFragmentBytes,
            WebRtcMediaSession.GetBulkFragmentPayloadBytes(allocation, hasInteractiveTraffic: true));
        Assert.Equal(
            expectedQueueBytes,
            WebRtcMediaSession.GetBulkQueueBudgetBytes(allocation, hasInteractiveTraffic: true));
    }

    [Fact]
    public void Unmeasured_bulk_uses_safe_interactive_startup_and_fast_file_only_defaults()
    {
        var allocation = new TransferAllocationPolicy(2UL * 1024 * 1024);

        Assert.Equal(
            2 * 1024,
            WebRtcMediaSession.GetBulkFragmentPayloadBytes(allocation, hasInteractiveTraffic: true));
        Assert.Equal(
            0UL,
            WebRtcMediaSession.GetBulkQueueBudgetBytes(allocation, hasInteractiveTraffic: true));
        Assert.Equal(
            64 * 1024,
            WebRtcMediaSession.GetBulkFragmentPayloadBytes(allocation, hasInteractiveTraffic: false));
        Assert.Equal(
            allocation.MaximumBulkBufferedBytes,
            WebRtcMediaSession.GetBulkQueueBudgetBytes(allocation, hasInteractiveTraffic: false));
    }

    [Fact]
    public void Video_frame_timing_sdp_feature_is_exact_and_idempotent()
    {
        const string plain = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\n";

        var advertised = WebRtcMediaSession.AdvertiseVideoFrameTelemetry(plain);

        Assert.True(WebRtcMediaSession.HasVideoFrameTelemetry(advertised));
        Assert.Equal(advertised, WebRtcMediaSession.AdvertiseVideoFrameTelemetry(advertised));
        Assert.False(WebRtcMediaSession.HasVideoFrameTelemetry(
            advertised.Replace("a=x-peeronq-video-frame-timing:1", "a=x-peeronq-video-frame-timing:10")));

        var inputAdvertised = WebRtcMediaSession.AdvertiseInputAcknowledgement(plain);
        Assert.True(WebRtcMediaSession.HasInputAcknowledgement(inputAdvertised));
        Assert.Equal(inputAdvertised, WebRtcMediaSession.AdvertiseInputAcknowledgement(inputAdvertised));
        Assert.False(WebRtcMediaSession.HasInputAcknowledgement(
            inputAdvertised.Replace("a=x-peeronq-input-ack:1", "a=x-peeronq-input-ack:10")));
        Assert.Equal(64UL * 1024, WebRtcMediaSession.MaximumInputBufferedBytes);

        var bulkAdvertised = WebRtcMediaSession.AdvertiseBulkDataLane(plain);
        Assert.True(WebRtcMediaSession.HasBulkDataLane(bulkAdvertised));
        Assert.Equal(bulkAdvertised, WebRtcMediaSession.AdvertiseBulkDataLane(bulkAdvertised));
        Assert.False(WebRtcMediaSession.HasBulkDataLane(
            bulkAdvertised.Replace("a=x-peeronq-bulk-data-lane:1", "a=x-peeronq-bulk-data-lane:10")));

        var nativeBulkAdvertised = WebRtcMediaSession.AdvertiseNativeBulkTransport(plain);
        Assert.True(WebRtcMediaSession.HasNativeBulkTransport(nativeBulkAdvertised));
        Assert.Equal(
            nativeBulkAdvertised,
            WebRtcMediaSession.AdvertiseNativeBulkTransport(nativeBulkAdvertised));
        Assert.False(WebRtcMediaSession.HasNativeBulkTransport(
            nativeBulkAdvertised.Replace("a=x-peeronq-native-bulk:1", "a=x-peeronq-native-bulk:10")));

        const string auxiliaryDescription = "v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n";
        var dedicatedInput = WebRtcMediaSession.AdvertiseInputDataLane(plain, auxiliaryDescription);
        Assert.True(WebRtcMediaSession.HasInputDataLane(dedicatedInput));
        Assert.True(WebRtcMediaSession.TryGetInputDescription(dedicatedInput, out var decodedInput));
        Assert.Equal(auxiliaryDescription, decodedInput);
        Assert.Equal(
            dedicatedInput,
            WebRtcMediaSession.AdvertiseInputDataLane(dedicatedInput, auxiliaryDescription));
        Assert.False(WebRtcMediaSession.TryGetInputDescription(
            dedicatedInput + "a=x-peeronq-input-description:invalid\r\n",
            out _));
        Assert.DoesNotContain(
            "x-peeronq-input-description",
            WebRtcMediaSession.RemoveInputDescription(dedicatedInput),
            StringComparison.Ordinal);

        var dedicatedBulk = WebRtcMediaSession.AdvertiseBulkDataLane(plain, auxiliaryDescription);
        Assert.True(WebRtcMediaSession.TryGetBulkDescription(dedicatedBulk, out var decodedBulk));
        Assert.Equal(auxiliaryDescription, decodedBulk);
        Assert.Equal(
            dedicatedBulk,
            WebRtcMediaSession.AdvertiseBulkDataLane(dedicatedBulk, auxiliaryDescription));
        Assert.False(WebRtcMediaSession.TryGetBulkDescription(
            dedicatedBulk + "a=x-peeronq-bulk-description:invalid\r\n",
            out _));
        Assert.DoesNotContain(
            "x-peeronq-bulk-description",
            WebRtcMediaSession.RemoveBulkDescription(dedicatedBulk),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Video_frame_timing_stays_disabled_when_an_older_peer_does_not_echo_it()
    {
        var sessionId = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture: null,
            MediaProfile.Conservative);
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId);

        var offer = await sharer.CreateOfferAsync();
        var legacyOffer = offer.Replace(
            "a=x-peeronq-video-frame-timing:1\r\n",
            string.Empty,
            StringComparison.Ordinal);
        legacyOffer = legacyOffer.Replace(
            "a=x-peeronq-input-ack:1\r\n",
            string.Empty,
            StringComparison.Ordinal);
        legacyOffer = legacyOffer.Replace(
            "a=x-peeronq-bulk-data-lane:1\r\n",
            string.Empty,
            StringComparison.Ordinal);
        legacyOffer = legacyOffer.Replace(
            "a=x-peeronq-native-bulk:1\r\n",
            string.Empty,
            StringComparison.Ordinal);
        var answer = await viewer.CreateAnswerAsync(legacyOffer);
        await sharer.ApplyRemoteAnswerAsync(answer);

        Assert.False(viewer.IsVideoFrameTelemetryNegotiated);
        Assert.False(sharer.IsVideoFrameTelemetryNegotiated);
        Assert.False(viewer.IsInputAcknowledgementNegotiated);
        Assert.False(sharer.IsInputAcknowledgementNegotiated);
        Assert.False(viewer.IsBulkDataLaneNegotiated);
        Assert.False(sharer.IsBulkDataLaneNegotiated);
        Assert.False(viewer.IsNativeBulkTransportNegotiated);
        Assert.False(sharer.IsNativeBulkTransportNegotiated);
        Assert.False(WebRtcMediaSession.HasVideoFrameTelemetry(answer));
        Assert.False(WebRtcMediaSession.HasInputAcknowledgement(answer));
        Assert.False(WebRtcMediaSession.HasBulkDataLane(answer));
        Assert.False(WebRtcMediaSession.HasNativeBulkTransport(answer));
    }

    [Fact]
    public async Task Bounded_bulk_description_is_ignored_by_the_legacy_primary_sdp_parser()
    {
        var sessionId = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture: null,
            MediaProfile.Conservative,
            permissions: SessionPermission.FileTransfer);
        var offer = await sharer.CreateOfferAsync();
        Assert.True(WebRtcMediaSession.TryGetBulkDescription(offer, out _));

        var legacyPeer = new RTCPeerConnection(WebRtcMediaSession.DefaultConfiguration);
        try
        {
            Assert.Equal(
                SetDescriptionResultEnum.OK,
                legacyPeer.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = offer,
                }));
            var answer = legacyPeer.createAnswer();
            await legacyPeer.setLocalDescription(answer);
            await sharer.ApplyRemoteAnswerAsync(answer.sdp);

            Assert.False(sharer.IsBulkDataLaneNegotiated);
        }
        finally
        {
            legacyPeer.close();
        }
    }

    [Fact]
    public async Task Full_control_uses_video_and_an_authorized_input_data_channel()
    {
        var sessionId = SessionId.New();
        var permissions = Phase1SessionScope.FullControlPermissions;
        await using var capture = new SyntheticCaptureSource();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture,
            MediaProfile.Conservative,
            permissions: permissions);
        await using var viewer = WebRtcMediaSession.CreateViewer(
            sessionId,
            permissions: permissions);

        var inputIceCandidates = 0;
        sharer.LocalIceCandidate += async (_, candidate) =>
        {
            if (WebRtcMediaSession.IsInputIceCandidateSdpMid(candidate.SdpMid))
                Interlocked.Increment(ref inputIceCandidates);
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        };
        viewer.LocalIceCandidate += async (_, candidate) =>
        {
            if (WebRtcMediaSession.IsInputIceCandidateSdpMid(candidate.SdpMid))
                Interlocked.Increment(ref inputIceCandidates);
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        };

        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.DataChannelReady += (_, _) => sharerReady.TrySetResult();
        viewer.DataChannelReady += (_, _) => viewerReady.TrySetResult();
        viewer.DataMessageReceived += (_, payload) => received.TrySetResult(payload.ToArray());

        var offer = await sharer.CreateOfferAsync();
        Assert.Contains("m=application", offer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("m=video", offer, StringComparison.OrdinalIgnoreCase);
        Assert.True(WebRtcMediaSession.HasInputAcknowledgement(offer));
        Assert.Contains("a=x-peeronq-input-data-lane:1", offer, StringComparison.Ordinal);
        Assert.True(WebRtcMediaSession.HasBulkDataLane(offer));
        Assert.True(WebRtcMediaSession.HasNativeBulkTransport(offer));
        var answer = await viewer.CreateAnswerAsync(offer);
        Assert.True(WebRtcMediaSession.HasInputAcknowledgement(answer));
        Assert.Contains("a=x-peeronq-input-data-lane:1", answer, StringComparison.Ordinal);
        Assert.True(WebRtcMediaSession.HasBulkDataLane(answer));
        Assert.True(WebRtcMediaSession.HasNativeBulkTransport(answer));
        await sharer.ApplyRemoteAnswerAsync(answer);
        Assert.True(viewer.IsInputAcknowledgementNegotiated);
        Assert.True(sharer.IsInputAcknowledgementNegotiated);
        Assert.True(viewer.IsInputDataLaneNegotiated);
        Assert.True(sharer.IsInputDataLaneNegotiated);
        Assert.True(viewer.IsBulkDataLaneNegotiated);
        Assert.True(sharer.IsBulkDataLaneNegotiated);
        Assert.True(viewer.IsNativeBulkTransportNegotiated);
        Assert.True(sharer.IsNativeBulkTransportNegotiated);

        await Task.WhenAll(
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntilAsync(() => viewer.IsInputDataLaneReady && sharer.IsInputDataLaneReady);
        await WaitUntilAsync(() => viewer.IsBulkDataLaneReady && sharer.IsBulkDataLaneReady);
        Assert.True(Volatile.Read(ref inputIceCandidates) > 0);
        Assert.True(sharer.UsesDedicatedInputPeerConnection);
        Assert.True(viewer.UsesDedicatedInputPeerConnection);
        Assert.True(sharer.UsesDedicatedBulkPeerConnection);
        Assert.True(viewer.UsesDedicatedBulkPeerConnection);
        var laneStatistics = viewer.GetStatistics();
        Assert.True(laneStatistics.InputDataLaneNegotiated);
        Assert.True(laneStatistics.InputDataLaneReady);
        Assert.True(laneStatistics.BulkDataLaneNegotiated);
        Assert.True(laneStatistics.BulkDataLaneReady);
        Assert.True(laneStatistics.SctpAssociationBufferedBytes >= 0);
        var expected = "phase5-input-data-channel"u8.ToArray();
        await sharer.SendDataAsync(expected);
        Assert.Equal(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var laneError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.SecurityError += (_, error) => laneError.TrySetResult(error);
        var laneKey = RandomNumberGenerator.GetBytes(64);
        using (var laneProtector = new SessionTrafficProtector(laneKey, sessionId, SessionRole.Sharer))
        {
            CryptographicOperations.ZeroMemory(laneKey);
            var misplacedFileRecord = laneProtector.Protect(
                SecureChannelKind.FileTransfer,
                Guid.NewGuid(),
                "misplaced-file-record"u8);
            try
            {
                await sharer.SendDataAsync(
                    misplacedFileRecord,
                    priority: DataMessagePriority.Interactive);
                Assert.Equal(
                    "secure_data_lane_context_mismatch",
                    await laneError.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(misplacedFileRecord);
            }
        }

        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(
            viewer, permissions, identities.Viewer, identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(
            sharer, permissions, identities.Sharer, identities.ViewerLegacyFingerprint);
        await WaitUntilAsync(() => viewerTransport.IsReady && sharerTransport.IsReady);
        await WaitUntilAsync(() => viewerTransport.PeerClockEstimate is not null);
        var framesReceived = 0;
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            Interlocked.Increment(ref framesReceived);
            viewer.ReportFrameRendered(new PresentedVideoFrame(
                frame.PipelineDecodedTimestamp,
                frame.Width,
                frame.Height,
                frame.CaptureTimestampUnixMicroseconds,
                frame.CaptureSequenceNumber,
                frame.PeerClockEstimate));
        };
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(
                CaptureTargetKind.Display,
                "full-control",
                "Full Control Synthetic Display",
                320,
                240),
            MaxFramesPerSecond = 15,
        });
        await WaitUntilAsync(() => Volatile.Read(ref framesReceived) > 0);
        var inputSink = new FakeRemoteInputSink();
        inputSink.RestoreApprovedScope(SessionPermission.ControlInput);
        await using var inputSender = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
        await using var inputReceiver = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, inputSink);
        await inputSender.SetLocalCaptureEnabledAsync(true);
        await inputSender.SendPointerMoveAsync(0.25, 0.75);
        await inputSender.SendPointerButtonAsync(0.25, 0.75, RemotePointerButton.Left, isPressed: true);
        await inputSender.SendPointerButtonAsync(0.25, 0.75, RemotePointerButton.Left, isPressed: false);
        await inputSender.SendKeyAsync(0x41, isPressed: true, isExtendedKey: false);
        await WaitUntilAsync(() => inputSink.Inputs.Count == 4);
        await WaitUntilAsync(() => viewer.GetStatistics().InputToInjectionLatencyP95Ms > 0);
        Assert.Collection(
            inputSink.Inputs,
            input =>
            {
                Assert.Equal(RemoteInputEventKind.PointerMove, input.Kind);
                Assert.Equal(0.25, input.NormalizedX);
                Assert.Equal(0.75, input.NormalizedY);
            },
            input =>
            {
                Assert.Equal(RemoteInputEventKind.PointerButton, input.Kind);
                Assert.Equal(RemotePointerButton.Left, input.Button);
                Assert.True(input.IsPressed);
            },
            input =>
            {
                Assert.Equal(RemoteInputEventKind.PointerButton, input.Kind);
                Assert.Equal(RemotePointerButton.Left, input.Button);
                Assert.False(input.IsPressed);
            },
            input =>
            {
                Assert.Equal(RemoteInputEventKind.Key, input.Kind);
                Assert.Equal((ushort)0x41, input.VirtualKey);
            });
        var dedicatedInputRecords = viewer.GetStatistics().InputDataRecordsSent;
        var framesBeforeInputFailure = Volatile.Read(ref framesReceived);
        CloseDedicatedInputPeer(viewer);
        await WaitUntilAsync(() => !viewer.IsInputDataLaneReady);
        await inputSender.SendPointerMoveAsync(0.5, 0.5);
        await WaitUntilAsync(() => inputSink.Inputs.Count == 5);
        await WaitUntilAsync(() => Volatile.Read(ref framesReceived) > framesBeforeInputFailure);
        Assert.True(viewer.IsDataChannelReady);
        Assert.True(sharer.IsDataChannelReady);
        Assert.Equal(MediaConnectionState.Connected, viewer.State);
        Assert.Equal(MediaConnectionState.Connected, sharer.State);
        Assert.Equal(dedicatedInputRecords, viewer.GetStatistics().InputDataRecordsSent);
        var inputStatistics = viewer.GetStatistics();
        Assert.True(inputStatistics.InputDataRecordsSent >= 4);
        Assert.True(inputStatistics.InputSctpBufferedBytes >= 0);
        Assert.True(inputStatistics.InputToInjectionLatencyP50Ms > 0);
        Assert.True(inputStatistics.InputToInjectionLatencyP95Ms > 0);
        Assert.True(inputStatistics.InputToInjectionLatencyP99Ms > 0);
        Assert.InRange(inputStatistics.InputClockUncertaintyMs, 0, 50);

        await using var viewOnlySharer = WebRtcMediaSession.CreateSharer(
            SessionId.New(),
            capture: null,
            MediaProfile.Conservative,
            permissions: SessionPermission.ViewScreen);
        var viewOnlyOffer = await viewOnlySharer.CreateOfferAsync();
        Assert.Contains("m=application", viewOnlyOffer, StringComparison.OrdinalIgnoreCase);
        Assert.False(WebRtcMediaSession.HasInputAcknowledgement(viewOnlyOffer));
        Assert.False(WebRtcMediaSession.HasInputDataLane(viewOnlyOffer));
        Assert.False(WebRtcMediaSession.HasBulkDataLane(viewOnlyOffer));
        Assert.False(WebRtcMediaSession.HasNativeBulkTransport(viewOnlyOffer));
    }

    [Fact]
    public async Task Full_control_falls_back_to_primary_when_input_lane_is_not_echoed()
    {
        var sessionId = SessionId.New();
        var permissions = Phase1SessionScope.FullControlPermissions;
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture: null,
            MediaProfile.Conservative,
            permissions: permissions);
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId, permissions: permissions);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);

        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.DataChannelReady += (_, _) => sharerReady.TrySetResult();
        viewer.DataChannelReady += (_, _) => viewerReady.TrySetResult();
        sharer.DataMessageReceived += (_, payload) => received.TrySetResult(payload.ToArray());

        var offer = await sharer.CreateOfferAsync();
        var legacyOffer = WebRtcMediaSession.RemoveInputDescription(offer)
            .Replace("a=x-peeronq-input-data-lane:1\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("a=x-peeronq-input-data-lane:1\n", string.Empty, StringComparison.Ordinal);
        var answer = await viewer.CreateAnswerAsync(legacyOffer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await Task.WhenAll(
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.False(viewer.IsInputDataLaneNegotiated);
        Assert.False(sharer.IsInputDataLaneNegotiated);
        var key = RandomNumberGenerator.GetBytes(64);
        using var protector = new SessionTrafficProtector(key, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(key);
        var protectedInput = protector.Protect(
            SecureChannelKind.Input,
            Guid.Empty,
            "legacy-input-fallback"u8);
        try
        {
            await viewer.SendDataAsync(protectedInput, priority: DataMessagePriority.Interactive);
            Assert.Equal(protectedInput, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, viewer.GetStatistics().InputDataRecordsSent);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedInput);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task File_transfer_flows_over_the_authorized_data_only_media_session(
        bool negotiateBulkDataLane)
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "peeronq-phase4-media", Guid.NewGuid().ToString("N"), "source");
        var destinationRoot = Path.Combine(Path.GetDirectoryName(sourceRoot)!, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        try
        {
            var source = Path.Combine(sourceRoot, "actual-channel.bin");
            var requestedSize = GetEnvironmentVariable("PEERONQ_LIVE_FILE_TRANSFER_BYTES");
            var fileSize = string.IsNullOrWhiteSpace(requestedSize)
                ? 180_000L
                : long.Parse(requestedSize, System.Globalization.CultureInfo.InvariantCulture);
            await WritePatternFileAsync(source, fileSize);

            IceConfiguration? iceConfiguration = null;
            if (string.Equals(
                    GetEnvironmentVariable("PEERONQ_LIVE_FILE_TRANSFER_TEST"),
                    "1",
                    StringComparison.Ordinal))
            {
                iceConfiguration = new IceConfiguration
                {
                    Servers =
                    [
                        new IceServerDefinition
                        {
                            Urls = [GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_URL")],
                            Username = GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_USERNAME"),
                            Credential = GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_CREDENTIAL"),
                            CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4),
                        },
                    ],
                    TransportPolicy = IceTransportPolicy.RelayOnly,
                    RelayServerId = "turn-local-1",
                    RelayRegion = "local",
                };
            }

            var sessionId = SessionId.New();
            await using var sharer = WebRtcMediaSession.CreateSharer(
                sessionId,
                null,
                MediaProfile.Conservative,
                configuration: iceConfiguration is null
                    ? null
                    : WebRtcMediaSession.BuildConfiguration(iceConfiguration),
                iceConfiguration: iceConfiguration,
                permissions: SessionPermission.FileTransfer);
            await using var viewer = WebRtcMediaSession.CreateViewer(
                sessionId,
                configuration: iceConfiguration is null
                    ? null
                    : WebRtcMediaSession.BuildConfiguration(iceConfiguration),
                iceConfiguration: iceConfiguration,
                permissions: SessionPermission.FileTransfer);
            var sharerBulkCandidates = 0;
            sharer.LocalIceCandidate += async (_, candidate) =>
            {
                if (WebRtcMediaSession.IsBulkIceCandidateSdpMid(candidate.SdpMid))
                    Interlocked.Increment(ref sharerBulkCandidates);
                await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
            };
            viewer.LocalIceCandidate += async (_, candidate) =>
                await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);

            await using var identities = await TestHybridIdentityPair.CreateAsync();
            await using var senderTransport = new MediaCollaborationTransport(
                sharer,
                SessionPermission.FileTransfer,
                identities.Sharer,
                identities.ViewerLegacyFingerprint);
            await using var receiverTransport = new MediaCollaborationTransport(
                viewer,
                SessionPermission.FileTransfer,
                identities.Viewer,
                identities.SharerLegacyFingerprint);
            await using var sender = new FileTransferService(senderTransport);
            await using var receiver = new FileTransferService(receiverTransport);
            var readyLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyRight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            senderTransport.Ready += (_, _) => readyLeft.TrySetResult();
            receiverTransport.Ready += (_, _) => readyRight.TrySetResult();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.IncomingOffer += async (_, offer) =>
                await receiver.AcceptAsync(offer.TransferId, destinationRoot, TransferCollisionPolicy.Rename);
            receiver.TransferChanged += (_, transfer) =>
            {
                if (transfer.Status == TransferStatus.Completed) received.TrySetResult();
            };

            var offer = await sharer.CreateOfferAsync();
            Assert.Equal(0, Volatile.Read(ref sharerBulkCandidates));
            if (!negotiateBulkDataLane)
            {
                offer = offer.Replace(
                    "a=x-peeronq-bulk-data-lane:1\r\n",
                    string.Empty,
                    StringComparison.Ordinal);
            }
            var answer = await viewer.CreateAnswerAsync(offer);
            await sharer.ApplyRemoteAnswerAsync(answer);
            Assert.Equal(negotiateBulkDataLane, viewer.IsBulkDataLaneNegotiated);
            Assert.Equal(negotiateBulkDataLane, sharer.IsBulkDataLaneNegotiated);
            await Task.WhenAll(
                readyLeft.Task.WaitAsync(TimeSpan.FromSeconds(30)),
                readyRight.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            if (negotiateBulkDataLane)
                await WaitUntilAsync(() => Volatile.Read(ref sharerBulkCandidates) > 0);
            else
                Assert.Equal(0, Volatile.Read(ref sharerBulkCandidates));

            if (iceConfiguration is not null)
            {
                Assert.Equal(ConnectionPath.Relayed, sharer.GetStatistics().ConnectionPath);
                await Assert.ThrowsAsync<InvalidOperationException>(() => sender.OfferAsync([source]));
                return;
            }

            var transferClock = Stopwatch.StartNew();
            await sender.OfferAsync([source]);
            var completionTimeout = fileSize > 8L * 1024 * 1024
                ? TimeSpan.FromMinutes(15)
                : TimeSpan.FromSeconds(30);
            try
            {
                await received.Task.WaitAsync(completionTimeout);
            }
            catch (TimeoutException)
            {
                var senderSnapshot = sender.History.SingleOrDefault();
                var receiverSnapshot = receiver.History.SingleOrDefault();
                var timeoutStatistics = sharer.GetStatistics();
                Assert.Fail(
                    $"File transfer timed out: bulkLane={negotiateBulkDataLane} " +
                    $"sender={senderSnapshot?.Status}:{senderSnapshot?.TransferredBytes} " +
                    $"receiver={receiverSnapshot?.Status}:{receiverSnapshot?.TransferredBytes} " +
                    $"bulkReady={timeoutStatistics.BulkDataLaneReady} " +
                    $"bulkRecords={timeoutStatistics.BulkDataRecordsSent} " +
                    $"bulkFragments={timeoutStatistics.BulkDataFragmentsSent} " +
                    $"bulkBuffered={timeoutStatistics.BulkSctpBufferedBytes}");
            }
            transferClock.Stop();
            var receivedPath = Path.Combine(destinationRoot, "actual-channel.bin");
            Assert.Equal(fileSize, new FileInfo(receivedPath).Length);
            await using var sourceStream = File.OpenRead(source);
            await using var receivedStream = File.OpenRead(receivedPath);
            Assert.Equal(
                await SHA256.HashDataAsync(sourceStream),
                await SHA256.HashDataAsync(receivedStream));
            var transferStatistics = sharer.GetStatistics();
            Assert.Equal(negotiateBulkDataLane, transferStatistics.BulkDataLaneNegotiated);
            if (negotiateBulkDataLane)
            {
                Assert.True(transferStatistics.BulkDataRecordsSent > 0);
                Assert.True(
                    transferStatistics.BulkDataFragmentsSent > transferStatistics.BulkDataRecordsSent,
                    $"records={transferStatistics.BulkDataRecordsSent} fragments={transferStatistics.BulkDataFragmentsSent}");
                Assert.InRange(transferStatistics.BulkDataFragmentBytes, 1, 64 * 1024);
                Assert.True(transferStatistics.BulkQueueBudgetBytes >= 0);
            }
            else
            {
                Assert.Equal(0, transferStatistics.BulkDataRecordsSent);
                Assert.Equal(0, transferStatistics.BulkDataFragmentsSent);
            }
            output.WriteLine(
                $"bulkLane={negotiateBulkDataLane} fileBytes={fileSize} " +
                $"elapsedSeconds={transferClock.Elapsed.TotalSeconds:F1} " +
                $"throughputKiBps={fileSize / 1024d / transferClock.Elapsed.TotalSeconds:F1}");

        }
        finally
        {
            var testRoot = Directory.GetParent(sourceRoot)!.FullName;
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Interactive_input_remains_responsive_while_bulk_transfer_is_backpressured()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "peeronq-phase4-priority", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(destination);
        var source = Path.Combine(testRoot, "priority.bin");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(1024L * 1024);

        try
        {
            await using var capture = new SyntheticCaptureSource();
            await capture.StartAsync(new CaptureRequest
            {
                Target = new CaptureTargetInfo(
                    CaptureTargetKind.Display,
                    "priority-synthetic",
                    "Priority Synthetic Display",
                    320,
                    240),
                MaxFramesPerSecond = 30,
            });
            var sessionId = SessionId.New();
            var permissions = SessionPermission.ViewScreen |
                              SessionPermission.ControlInput |
                              SessionPermission.FileTransfer;
            await using var sharer = WebRtcMediaSession.CreateSharer(
                sessionId,
                capture,
                MediaProfile.Conservative,
                permissions: permissions);
            await using var viewer = WebRtcMediaSession.CreateViewer(sessionId, permissions: permissions);
            sharer.LocalIceCandidate += async (_, candidate) =>
                await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
            viewer.LocalIceCandidate += async (_, candidate) =>
                await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);

            await using var identities = await TestHybridIdentityPair.CreateAsync();
            await using var viewerTransport = new MediaCollaborationTransport(
                viewer, permissions, identities.Viewer, identities.SharerLegacyFingerprint);
            await using var sharerTransport = new MediaCollaborationTransport(
                sharer, permissions, identities.Sharer, identities.ViewerLegacyFingerprint);
            await using var sender = new FileTransferService(
                sharerTransport,
                new FileTransferOptions { ChunkBytes = 1024 });
            await using var receiver = new FileTransferService(
                viewerTransport,
                new FileTransferOptions { ChunkBytes = 1024 });
            var inputSink = new FakeRemoteInputSink();
            inputSink.RestoreApprovedScope(SessionPermission.ControlInput);
            await using var inputSender = new RemoteInputSession(sessionId, SessionRole.Viewer, viewerTransport);
            await using var inputReceiver = new RemoteInputSession(sessionId, SessionRole.Sharer, sharerTransport, inputSink);
            var framesReceived = 0;
            var videoLatencySamples = new List<(long ReceivedAt, double LatencyMs)>();
            var firstFrame = new TaskCompletionSource<RemoteVideoFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.RemoteFrameReceived += (_, frame) =>
            {
                var receivedAt = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref framesReceived);
                if (capture.TryGetCaptureTimestamp(frame.CaptureSequenceNumber, out var capturedAt))
                {
                    lock (videoLatencySamples)
                    {
                        videoLatencySamples.Add((
                            receivedAt,
                            Stopwatch.GetElapsedTime(capturedAt, receivedAt).TotalMilliseconds));
                    }
                }
                viewer.ReportFrameRendered(new PresentedVideoFrame(
                    frame.PipelineDecodedTimestamp,
                    frame.Width,
                    frame.Height,
                    frame.CaptureTimestampUnixMicroseconds,
                    frame.CaptureSequenceNumber,
                    frame.PeerClockEstimate));
                firstFrame.TrySetResult(frame);
            };
            var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewerTransport.Ready += (_, _) => viewerReady.TrySetResult();
            sharerTransport.Ready += (_, _) => sharerReady.TrySetResult();
            var transferring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.TransferChanged += (_, snapshot) =>
            {
                if (snapshot.Status == TransferStatus.Transferring) transferring.TrySetResult();
            };
            receiver.TransferChanged += (_, snapshot) =>
            {
                if (snapshot.Status == TransferStatus.Completed) completed.TrySetResult();
            };
            receiver.IncomingOffer += async (_, offer) =>
                await receiver.AcceptAsync(offer.TransferId, destination, TransferCollisionPolicy.Rename);

            var offer = await sharer.CreateOfferAsync();
            var answer = await viewer.CreateAnswerAsync(offer);
            await sharer.ApplyRemoteAnswerAsync(answer);
            await Task.WhenAll(
                viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)),
                sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            await WaitUntilAsync(() => viewer.IsInputDataLaneReady && sharer.IsInputDataLaneReady);
            await WaitUntilAsync(() => viewer.IsBulkDataLaneReady && sharer.IsBulkDataLaneReady);
            Assert.True(viewer.IsInputDataLaneNegotiated);
            Assert.True(sharer.IsInputDataLaneNegotiated);
            Assert.True(viewer.IsBulkDataLaneNegotiated);
            Assert.True(sharer.IsBulkDataLaneNegotiated);
            Assert.True(viewer.UsesDedicatedInputPeerConnection);
            Assert.True(sharer.UsesDedicatedInputPeerConnection);
            Assert.True(viewer.UsesDedicatedBulkPeerConnection);
            Assert.True(sharer.UsesDedicatedBulkPeerConnection);
            await WaitUntilAsync(() => viewerTransport.PeerClockEstimate is not null);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await inputSender.SetLocalCaptureEnabledAsync(true);

            var inputSendSamples = new List<double>();
            async Task<double> MeasureInputInjectionAsync(double position)
            {
                var injected = new TaskCompletionSource<long>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                inputSink.InputInjected = _ => injected.TrySetResult(Stopwatch.GetTimestamp());
                var startedAt = Stopwatch.GetTimestamp();
                await inputSender.SendPointerMoveAsync(position, position);
                inputSendSamples.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                var injectedAt = await injected.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // Measure the injection boundary, not scheduling the test continuation after it.
                return Stopwatch.GetElapsedTime(startedAt, injectedAt).TotalMilliseconds;
            }

            static double P95(IEnumerable<double> samples)
            {
                var ordered = samples.Order().ToArray();
                return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
            }

            // Keep JIT/first-dispatch startup outside the steady-state interactive acceptance
            // distribution without relaxing the production 35 ms p95 budget.
            for (var index = 0; index < 5; index++)
                await MeasureInputInjectionAsync((index + 1) / 10d);

            var baselineInputSamples = new List<double>(20);
            for (var index = 0; index < 20; index++)
                baselineInputSamples.Add(await MeasureInputInjectionAsync((index + 1) / 25d));
            var baselineInputP95 = P95(baselineInputSamples);
            output.WriteLine($"inputSendP95Ms={P95(inputSendSamples.Skip(5)):F1} baselineInjectionP95Ms={baselineInputP95:F1}");
            await Task.Delay(150);

            await sender.OfferAsync([source]);
            await transferring.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(completed.Task.IsCompleted);
            var bulkStartedAt = Stopwatch.GetTimestamp();
            var framesBeforeInput = Volatile.Read(ref framesReceived);
            var bulkInputSamples = new List<double>(20);
            for (var index = 0; index < 20; index++)
                bulkInputSamples.Add(await MeasureInputInjectionAsync((20 - index) / 25d));
            var bulkInputP95 = P95(bulkInputSamples);
            await WaitUntilAsync(() => viewer.GetStatistics().InputToInjectionLatencyP95Ms > 0);

            Assert.False(completed.Task.IsCompleted);
            Assert.True(
                baselineInputP95 <= ConnectionPolicyProvider.TargetInputToInjectionP95Ms,
                $"Baseline input injection p95 was {baselineInputP95:F1}ms.");
            Assert.True(
                bulkInputP95 <= ConnectionPolicyProvider.TargetInputToInjectionP95Ms,
                $"Bulk-time input injection p95 was {bulkInputP95:F1}ms.");
            Assert.True(
                bulkInputP95 - baselineInputP95 <= 10,
                $"Bulk inflated input injection p95 from {baselineInputP95:F1}ms to {bulkInputP95:F1}ms.");
            Assert.True(
                bulkInputSamples.Max() < 250,
                $"Bulk-time input stalled for {bulkInputSamples.Max():F1}ms.");
            var viewerStatistics = viewer.GetStatistics();
            var inputLatency = viewerStatistics.InputToInjectionLatencyP95Ms;
            var inputUncertainty = viewerStatistics.InputClockUncertaintyMs;
            Assert.True(viewerStatistics.InputDataRecordsSent >= 45);
            Assert.InRange(viewerStatistics.InputSctpBufferedBytes, 0, 64 * 1024);
            Assert.True(inputLatency > 0);
            if (inputUncertainty <= ConnectionPolicyProvider.MaximumAdaptiveInputClockUncertaintyMs)
            {
                Assert.True(
                    inputLatency <= ConnectionPolicyProvider.TargetInputToInjectionP95Ms,
                    $"measured={inputLatency:F1}ms wallP95={bulkInputP95:F1}ms " +
                    $"uncertainty={inputUncertainty:F1}ms");
            }
            sharer.ReportRemoteQualityFeedback(new RemoteSessionQualityFeedback(
                DecodeFps: 30,
                RenderFps: 30,
                DecodeToRenderLatencyP95Ms: 10,
                CaptureToPresentLatencyP95Ms: 20,
                FrameAgeClockUncertaintyMs: 1,
                InputToInjectionLatencyP95Ms: 60,
                InputClockUncertaintyMs: 2));
            Assert.Equal(TimeSpan.FromSeconds(5), WebRtcMediaSession.RemoteQualityFeedbackLifetime);
            Assert.True(
                await UntilAsync(
                    () => sharer.GetStatistics().QualityChangeReason == "viewer_input_latency",
                    TimeSpan.FromSeconds(4)),
                "reverse-direction sender did not adapt to fresh viewer input pressure");
            var sharerStatistics = sharer.GetStatistics();
            Assert.Equal(60, sharerStatistics.InputToInjectionLatencyP95Ms);
            var pressuredAllocation = ConnectionPolicyProvider.GetTransferAllocation(
                sharerTransport.TransferPriorityMode,
                sharerStatistics,
                hasInteractiveTraffic: true);
            Assert.Equal(64UL * 1024, pressuredAllocation.MaximumBulkBufferedBytes);
            Assert.Equal(128, pressuredAllocation.MaximumBulkKbps);
            Assert.True(
                await UntilAsync(
                    () => Volatile.Read(ref framesReceived) >= framesBeforeInput + 3,
                    TimeSpan.FromSeconds(2)),
                "Video stopped advancing while the dedicated bulk transfer was active.");
            Assert.False(completed.Task.IsCompleted);
            double[] videoLatenciesDuringBulk;
            lock (videoLatencySamples)
            {
                videoLatenciesDuringBulk = videoLatencySamples
                    .Where(sample => sample.ReceivedAt >= bulkStartedAt)
                    .Select(sample => sample.LatencyMs)
                    .ToArray();
            }
            Assert.True(videoLatenciesDuringBulk.Length >= 3);
            Assert.InRange(videoLatenciesDuringBulk.Max(), 0.001, 75);
            output.WriteLine($"inputBaselineP95Ms={baselineInputP95:F1} " +
                             $"inputDuringTransferP95Ms={bulkInputP95:F1} " +
                             $"measuredInputToInjectionP95Ms={inputLatency:F1} " +
                             $"captureToDecodeMaxMs={videoLatenciesDuringBulk.Max():F1} " +
                             $"fileBytes={new FileInfo(source).Length} chunkBytes=1024");
            Assert.True(
                await UntilAsync(
                    () => sharer.GetStatistics().InputToInjectionLatencyP95Ms == 0,
                    TimeSpan.FromSeconds(7)),
                "stale remote input pressure did not expire from the reverse-direction sender");
            await completed.Task.WaitAsync(TimeSpan.FromMinutes(3));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    /// <summary>
    /// Full media path on one machine: sharer encodes VP8 from captured frames, ICE and DTLS
    /// complete, and the viewer decodes real frames. Signaling is short-circuited in-process
    /// because the signaling path has its own integration tests.
    /// </summary>
    [Theory]
    [InlineData(320, 240, 15, true)]
    [InlineData(1920, 1080, 60, true)]
    [InlineData(3840, 2160, 5, false)]
    [InlineData(3840, 2160, 30, true)]
    public async Task Video_flows_from_a_sharer_to_a_viewer_over_a_real_peer_connection(
        int width,
        int height,
        int targetFps,
        bool requireMultipleFrames)
    {
        await using var capture = new SyntheticCaptureSource(width, height);
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "Synthetic Display", width, height),
            MaxFramesPerSecond = targetFps,
        });

        var sessionId = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture,
            MediaProfile.For(QualityProfile.Quality) with { TargetFps = targetFps });
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId);
        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(
            viewer,
            SessionPermission.ViewScreen,
            identities.Viewer,
            identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(
            sharer,
            SessionPermission.ViewScreen,
            identities.Sharer,
            identities.ViewerLegacyFingerprint);

        // Relay ICE the way the signaling server would.
        sharer.LocalIceCandidate += async (_, c) =>
            await viewer.AddRemoteIceCandidateAsync(c.Candidate, c.SdpMid, c.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, c) =>
            await sharer.AddRemoteIceCandidateAsync(c.Candidate, c.SdpMid, c.SdpMLineIndex);

        var sharerConnected = new TaskCompletionSource();
        var viewerConnected = new TaskCompletionSource();
        sharer.StateChanged += (_, s) => { if (s == MediaConnectionState.Connected) sharerConnected.TrySetResult(); };
        viewer.StateChanged += (_, s) => { if (s == MediaConnectionState.Connected) viewerConnected.TrySetResult(); };

        var framesReceived = 0;
        double firstLocalFrameAgeMs = -1, lastLocalFrameAgeMs = 0;
        var firstFrame = new TaskCompletionSource<RemoteVideoFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            Interlocked.Increment(ref framesReceived);
            var localFrameAgeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                  - frame.CaptureTimestampUnixMicroseconds / 1000d;
            Interlocked.CompareExchange(ref firstLocalFrameAgeMs, localFrameAgeMs, -1);
            Volatile.Write(ref lastLocalFrameAgeMs, localFrameAgeMs);
            // The real viewer reports this only after WriteableBitmap.Invalidate succeeds.
            viewer.ReportFrameRendered(new PresentedVideoFrame(
                frame.PipelineDecodedTimestamp,
                frame.Width,
                frame.Height,
                frame.CaptureTimestampUnixMicroseconds,
                frame.CaptureSequenceNumber,
                frame.PeerClockEstimate));
            firstFrame.TrySetResult(frame);
        };

        var offer = await sharer.CreateOfferAsync();
        Assert.Contains("VP8", offer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("m=audio", offer);           // The current protocol never negotiates audio.
        Assert.Contains("m=application", offer);            // mandatory secure-session handshake
        Assert.True(WebRtcMediaSession.HasVideoFrameTelemetry(offer));

        var answer = await viewer.CreateAnswerAsync(offer);
        Assert.True(WebRtcMediaSession.HasVideoFrameTelemetry(answer));
        await sharer.ApplyRemoteAnswerAsync(answer);
        Assert.True(viewer.IsVideoFrameTelemetryNegotiated);
        Assert.True(sharer.IsVideoFrameTelemetryNegotiated);

        await Task.WhenAll(
            sharerConnected.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            viewerConnected.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        await WaitUntilAsync(() => viewerTransport.IsReady && sharerTransport.IsReady);

        output.WriteLine("Peer connections established.");

        var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.True(frame.Pixels.Length > 0);
        Assert.Equal(frame.ExpectedLength, frame.Pixels.Length);
        Assert.True(frame.PipelineDecodedTimestamp > 0);
        Assert.True(frame.CaptureTimestampUnixMicroseconds > 0);
        Assert.True(frame.CaptureSequenceNumber > 0);
        Assert.True(
            await UntilAsync(
                () => viewer.GetStatistics().CaptureToPresentLatencyP95Ms > 0,
                TimeSpan.FromSeconds(10)),
            "authenticated capture-to-present timing did not produce a bounded sample");

        if (requireMultipleFrames)
        {
            await Task.Delay(1500);
        }
        else
        {
            await capture.StopAsync();
        }

        var sharerStats = sharer.GetStatistics();
        var viewerStats = viewer.GetStatistics();
        output.WriteLine($"encoded={sharerStats.FramesEncoded} bytes={sharerStats.BytesSent} " +
                         $"rendered={viewerStats.FramesRendered} received={viewerStats.BytesReceived}");
        output.WriteLine($"resolution={width}x{height} targetFps={targetFps} " +
                         $"encodeFps={sharerStats.EncodeFps:F1} renderFps={viewerStats.RenderFps:F1} " +
                         $"captureToPresentP95Ms={viewerStats.CaptureToPresentLatencyP95Ms:F1} " +
                         $"clockUncertaintyMs={viewerStats.FrameAgeClockUncertaintyMs:F1}");
        output.WriteLine($"captureToEncodeP95Ms={sharerStats.CaptureToEncodeLatencyP95Ms:F1} " +
                         $"decodeToRenderP95Ms={viewerStats.DecodeToRenderLatencyP95Ms:F1} " +
                         $"decodeFps={viewerStats.DecodeFps:F1} dropped={sharerStats.FramesDropped} " +
                         $"firstLocalFrameAgeMs={Volatile.Read(ref firstLocalFrameAgeMs):F1} " +
                         $"lastLocalFrameAgeMs={Volatile.Read(ref lastLocalFrameAgeMs):F1}");

        Assert.True(sharerStats.FramesEncoded > 0, "the sharer encoded no frames");
        Assert.True(sharerStats.BytesSent > 0, "the sharer sent no bytes");
        Assert.True(viewerStats.FramesRendered > 0, "the viewer decoded no frames");
        Assert.True(viewerStats.CaptureToPresentLatencyP95Ms > 0);
        Assert.InRange(viewerStats.FrameAgeClockUncertaintyMs, 0, 50);
        if (width == 320)
        {
            var framesBeforePressure = Volatile.Read(ref framesReceived);
            sharer.ReportRemoteQualityFeedback(new RemoteSessionQualityFeedback(
                DecodeFps: 30,
                RenderFps: 30,
                DecodeToRenderLatencyP95Ms: 10,
                CaptureToPresentLatencyP95Ms: 150,
                FrameAgeClockUncertaintyMs: 1,
                InputToInjectionLatencyP95Ms: 0,
                InputClockUncertaintyMs: 0));

            Assert.True(
                await UntilAsync(
                    () => sharer.GetStatistics().QualityChangeReason == "viewer_frame_age",
                    TimeSpan.FromSeconds(4)),
                "real WebRTC sharer did not adapt to fresh authenticated viewer frame age");
            Assert.True(
                await UntilAsync(
                    () => Volatile.Read(ref framesReceived) > framesBeforePressure,
                    TimeSpan.FromSeconds(4)),
                "video stopped while viewer presentation feedback changed quality");

            var framesBeforeStallRecovery = Volatile.Read(ref framesReceived);
            sharer.ReportRemoteQualityFeedback(new RemoteSessionQualityFeedback(
                DecodeFps: 0,
                RenderFps: 0,
                DecodeToRenderLatencyP95Ms: 0,
                CaptureToPresentLatencyP95Ms: 0,
                FrameAgeClockUncertaintyMs: 0,
                InputToInjectionLatencyP95Ms: 0,
                InputClockUncertaintyMs: 0));
            Assert.True(
                await UntilAsync(
                    () => sharer.GetStatistics().QualityChangeReason == "viewer_video_stall",
                    TimeSpan.FromSeconds(4)),
                "fresh zero-presentation feedback did not trigger bounded stall recovery");
            Assert.True(
                await UntilAsync(
                    () => Volatile.Read(ref framesReceived) > framesBeforeStallRecovery,
                    TimeSpan.FromSeconds(4)),
                "video did not continue after receiver-stall keyframe recovery");
        }
        if (requireMultipleFrames)
        {
            Assert.True(framesReceived > 1, "only a single frame arrived");
        }
    }

    [LiveTurnFact]
    public async Task Relay_only_video_flows_through_the_live_turn_server()
    {
        var turnUrl = GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_URL");
        var username = GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_USERNAME");
        var credential = GetRequiredEnvironmentVariable("PEERONQ_LIVE_TURN_CREDENTIAL");
        var iceConfiguration = new IceConfiguration
        {
            Servers =
            [
                new IceServerDefinition
                {
                    Urls = [turnUrl],
                    Username = username,
                    Credential = credential,
                    CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4),
                },
            ],
            TransportPolicy = IceTransportPolicy.RelayOnly,
            RelayServerId = "turn-local-1",
            RelayRegion = "local",
        };

        await using var capture = new SyntheticCaptureSource();
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "Synthetic Display", 320, 240),
            MaxFramesPerSecond = 30,
        }, CancellationToken.None);

        var sessionId = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(
            sessionId,
            capture,
            MediaProfile.Conservative with { TargetFps = 15 },
            configuration: WebRtcMediaSession.BuildConfiguration(iceConfiguration),
            iceConfiguration: iceConfiguration);
        await using var viewer = WebRtcMediaSession.CreateViewer(
            sessionId,
            configuration: WebRtcMediaSession.BuildConfiguration(iceConfiguration),
            iceConfiguration: iceConfiguration);
        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(
            viewer,
            SessionPermission.ViewScreen,
            identities.Viewer,
            identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(
            sharer,
            SessionPermission.ViewScreen,
            identities.Sharer,
            identities.ViewerLegacyFingerprint);

        var relayCandidates = 0;
        sharer.LocalIceCandidate += async (_, candidate) =>
        {
            if (candidate.Candidate.Contains(" typ relay ", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref relayCandidates);
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        };
        viewer.LocalIceCandidate += async (_, candidate) =>
        {
            if (candidate.Candidate.Contains(" typ relay ", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref relayCandidates);
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        };

        var sharerConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFrame = new TaskCompletionSource<RemoteVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.StateChanged += (_, state) =>
        {
            if (state == MediaConnectionState.Connected) sharerConnected.TrySetResult();
        };
        viewer.StateChanged += (_, state) =>
        {
            if (state == MediaConnectionState.Connected) viewerConnected.TrySetResult();
        };
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            viewer.ReportFrameRendered(frame.PipelineDecodedTimestamp, frame.Width, frame.Height);
            firstFrame.TrySetResult(frame);
        };

        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);

        await Task.WhenAll(
            sharerConnected.Task.WaitAsync(TimeSpan.FromSeconds(45)),
            viewerConnected.Task.WaitAsync(TimeSpan.FromSeconds(45)));
        await WaitUntilAsync(() => viewerTransport.IsReady && sharerTransport.IsReady);
        var receivedFrame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(receivedFrame.PipelineDecodedTimestamp > 0);
        await Task.Delay(500);

        if (string.Equals(
                GetEnvironmentVariable("PEERONQ_LIVE_TURN_INTERRUPT_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            var framesBeforeInterruption = viewer.GetStatistics().FramesRendered;
            await InvokeDockerComposeAsync("pause", "turn");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await InvokeDockerComposeAsync("unpause", "turn");
            }

            var resumed = await UntilAsync(
                () => viewer.GetStatistics().FramesRendered > framesBeforeInterruption,
                TimeSpan.FromSeconds(15));
            Assert.True(resumed, "TURN-relayed media did not recover after a temporary relay outage");
        }

        var sharerStatistics = sharer.GetStatistics();
        var viewerStatistics = viewer.GetStatistics();
        output.WriteLine(
            $"relayCandidates={relayCandidates} sharerPath={sharerStatistics.ConnectionPath} " +
            $"viewerPath={viewerStatistics.ConnectionPath} rendered={viewerStatistics.FramesRendered}");

        Assert.True(relayCandidates >= 2, "both peers must gather relay candidates");
        Assert.Equal(ConnectionPath.Relayed, sharerStatistics.ConnectionPath);
        Assert.Equal(ConnectionPath.Relayed, viewerStatistics.ConnectionPath);
        Assert.Equal(320, receivedFrame.Width);
        Assert.Equal(240, receivedFrame.Height);
        Assert.True(viewerStatistics.FramesRendered > 0, "the viewer rendered no TURN-relayed frames");
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required live TURN setting: {name}.");

    private static string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    private static async Task WritePatternFileAsync(string path, long size)
    {
        if (size is < 1 or > 1024L * 1024 * 1024)
            throw new InvalidOperationException("Acceptance file size must be between 1 byte and 1 GiB.");

        var buffer = new byte[1024 * 1024];
        for (var index = 0; index < buffer.Length; index++) buffer[index] = (byte)(index % 241);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var remaining = size;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }
    }

    private static async Task InvokeDockerComposeAsync(params string[] command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetRequiredEnvironmentVariable("PEERONQ_LIVE_DOCKER_EXE"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("--env-file");
        startInfo.ArgumentList.Add(GetRequiredEnvironmentVariable("PEERONQ_LIVE_COMPOSE_ENV_FILE"));
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(GetRequiredEnvironmentVariable("PEERONQ_LIVE_COMPOSE_FILE"));
        foreach (var argument in command) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Docker Compose process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"Docker Compose {string.Join(' ', command)} failed with exit code {process.ExitCode}. {output} {error}");
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    [Fact]
    public async Task Closing_a_session_stops_the_media_and_releases_resources()
    {
        await using var capture = new SyntheticCaptureSource();
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "Synthetic", 320, 240),
        });

        var sharer = WebRtcMediaSession.CreateSharer(SessionId.New(), capture, MediaProfile.Conservative);
        await sharer.CreateOfferAsync();

        await sharer.CloseAsync();
        Assert.Equal(MediaConnectionState.Closed, sharer.State);

        await sharer.DisposeAsync();
        await sharer.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Repeated_sessions_do_not_accumulate_state()
    {
        for (var i = 0; i < 5; i++)
        {
            await using var capture = new SyntheticCaptureSource(160, 120);
            await capture.StartAsync(new CaptureRequest
            {
                Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "Synthetic", 160, 120),
            });

            await using var sharer = WebRtcMediaSession.CreateSharer(SessionId.New(), capture, MediaProfile.Conservative);
            await using var viewer = WebRtcMediaSession.CreateViewer(SessionId.New());

            var offer = await sharer.CreateOfferAsync();
            var answer = await viewer.CreateAnswerAsync(offer);
            await sharer.ApplyRemoteAnswerAsync(answer);
        }

        // Reaching here without an exception or a hang is the assertion: every session was
        // torn down cleanly by DisposeAsync.
        Assert.True(true);
    }

    [Fact]
    public async Task A_viewer_rejects_a_malformed_offer()
    {
        await using var viewer = WebRtcMediaSession.CreateViewer(SessionId.New());

        await Assert.ThrowsAnyAsync<Exception>(() => viewer.CreateAnswerAsync("this is not sdp"));
    }

    private sealed class TestHybridIdentityPair : IAsyncDisposable
    {
        private TestHybridIdentityPair(
            HybridDeviceIdentityService viewer,
            string viewerLegacyFingerprint,
            HybridDeviceIdentityService sharer,
            string sharerLegacyFingerprint)
        {
            Viewer = viewer;
            ViewerLegacyFingerprint = viewerLegacyFingerprint;
            Sharer = sharer;
            SharerLegacyFingerprint = sharerLegacyFingerprint;
        }

        public HybridDeviceIdentityService Viewer { get; }
        public string ViewerLegacyFingerprint { get; }
        public HybridDeviceIdentityService Sharer { get; }
        public string SharerLegacyFingerprint { get; }

        public static async Task<TestHybridIdentityPair> CreateAsync()
        {
            var viewer = await CreateIdentityAsync("Viewer");
            var sharer = await CreateIdentityAsync("Sharer");
            return new TestHybridIdentityPair(
                viewer.Service,
                viewer.LegacyFingerprint,
                sharer.Service,
                sharer.LegacyFingerprint);
        }

        private static async Task<(HybridDeviceIdentityService Service, string LegacyFingerprint)>
            CreateIdentityAsync(string displayName)
        {
            using var legacyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = legacyKey.ExportSubjectPublicKeyInfo();
            var secretStore = new MemorySecretStore();
            await secretStore.SetAsync(
                DeviceProvisioningService.SigningKeyName,
                legacyKey.ExportPkcs8PrivateKey());
            var identity = DeviceIdentity.Create(displayName) with
            {
                PublicKey = Convert.ToBase64String(publicKey),
            };
            var service = new HybridDeviceIdentityService(secretStore, identity);
            await service.GetPublicIdentityAsync();
            return (
                service,
                Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant());
        }

        public ValueTask DisposeAsync()
        {
            Viewer.Dispose();
            Sharer.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : IDeviceSecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(name, out var value) ? value.ToArray() : null);

        public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
        {
            _values[name] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRemoteInputSink : IRemoteInputSink
    {
        private bool _enabled;
        public List<RemoteInputEvent> Inputs { get; } = [];
        public Action<RemoteInputEvent>? InputInjected { get; set; }

        public void SetCaptureTarget(CaptureTargetInfo? target) { }
        public void RestoreApprovedScope(SessionPermission approvedPermissions) =>
            _enabled = approvedPermissions.HasFlag(SessionPermission.ControlInput);
        public void DisableAndReleaseAll() => _enabled = false;
        public void ReleaseAll() { }
        public bool TryInject(RemoteInputEvent input)
        {
            if (!_enabled) return false;
            Inputs.Add(input);
            InputInjected?.Invoke(input);
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private static void CloseDedicatedInputPeer(WebRtcMediaSession session)
    {
        var peerField = typeof(WebRtcMediaSession).GetField(
            "_inputPeer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var peer = Assert.IsType<RTCPeerConnection>(peerField?.GetValue(session));
        peer.close();
    }
}

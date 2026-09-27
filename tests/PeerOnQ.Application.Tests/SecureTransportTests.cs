using System.Net;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class SecureTransportTests
{
    [Fact]
    public void Peer_clock_estimate_uses_ntp_offset_and_network_uncertainty()
    {
        Assert.True(PeerClockEstimator.TryCalculate(
            localSentAtUnixMicroseconds: 1_000_000,
            remoteReceivedAtUnixMicroseconds: 1_060_000,
            remoteSentAtUnixMicroseconds: 1_062_000,
            localReceivedAtUnixMicroseconds: 1_024_000,
            out var estimate));

        Assert.Equal(49_000, estimate.RemoteMinusLocalOffsetMicroseconds);
        Assert.Equal(11_000, estimate.UncertaintyMicroseconds);
    }

    [Fact]
    public void Peer_clock_estimate_rejects_backwards_or_unbounded_samples()
    {
        Assert.False(PeerClockEstimator.TryCalculate(100, 200, 199, 300, out _));
        Assert.False(PeerClockEstimator.TryCalculate(100, 200, 201, 6_000_101, out _));
    }

    [Fact]
    public void Clock_sync_codec_is_bounded_versioned_and_round_trips_both_directions()
    {
        var request = ClockSyncCodec.EncodeRequest(7, 1_000_000);
        Assert.True(ClockSyncCodec.TryDecodeRequest(request, out var requestId, out var sentAt));
        Assert.Equal((ulong)7, requestId);
        Assert.Equal(1_000_000, sentAt);

        var reply = ClockSyncCodec.EncodeReply(7, 1_000_000, 1_060_000, 1_062_000);
        Assert.True(ClockSyncCodec.TryDecodeReply(
            reply,
            out requestId,
            out sentAt,
            out var receivedAt,
            out var repliedAt));
        Assert.Equal((ulong)7, requestId);
        Assert.Equal(1_000_000, sentAt);
        Assert.Equal(1_060_000, receivedAt);
        Assert.Equal(1_062_000, repliedAt);

        reply[4]++;
        Assert.False(ClockSyncCodec.TryDecodeReply(reply, out _, out _, out _, out _));
    }

    [Fact]
    public void Native_bulk_negotiation_codec_is_bounded_versioned_and_exact()
    {
        var fingerprint = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var clientHello = NativeBulkNegotiationCodec.EncodeClientHello(fingerprint);
        Assert.True(NativeBulkNegotiationCodec.IsFrame(clientHello));
        Assert.True(NativeBulkNegotiationCodec.TryDecodeClientHello(clientHello, out var decodedClientPin));
        Assert.Equal(fingerprint, decodedClientPin);

        var serverOffer = NativeBulkNegotiationCodec.EncodeServerOffer(fingerprint, 54_321);
        Assert.True(NativeBulkNegotiationCodec.TryDecodeServerOffer(
            serverOffer,
            out var decodedServerPin,
            out var port));
        Assert.Equal(fingerprint, decodedServerPin);
        Assert.Equal(54_321, port);

        serverOffer[4]++;
        Assert.False(NativeBulkNegotiationCodec.TryDecodeServerOffer(serverOffer, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeBulkNegotiationCodec.EncodeServerOffer(fingerprint, 0));
    }

    [Fact]
    public void Native_bulk_receipt_round_trips_as_a_bounded_control_record()
    {
        var transferId = Guid.NewGuid();
        var encoded = CollaborationProtocolCodec.Encode(new TransferReceipt
        {
            SessionId = Guid.NewGuid(),
            PermissionGeneration = 1,
            TransferId = transferId,
            DeliveredBytes = 4L * 1024 * 1024,
        });

        Assert.True(CollaborationProtocolCodec.TryDecode(encoded, out var decoded, out var error), error);
        var receipt = Assert.IsType<TransferReceipt>(decoded);
        Assert.Equal(transferId, receipt.TransferId);
        Assert.Equal(4L * 1024 * 1024, receipt.DeliveredBytes);
        Assert.True(encoded.Length < CollaborationProtocolCodec.MaximumControlBytes);
    }

    [Fact]
    public void Native_bulk_capacity_grows_from_confirmed_delivery_and_backs_off_for_input_pressure()
    {
        var time = new ManualTimestampTimeProvider();
        var estimator = new NativeBulkCapacityEstimator(time);
        var transferId = Guid.NewGuid();
        var baseline = new TransferAllocationPolicy(2UL * 1024 * 1024, 5_000, 256, 36_000);
        var healthy = new MediaStatistics { ConnectionHealth = ConnectionHealth.Excellent };
        const int deliveredPerSample = 1024 * 1024;
        long delivered = 0;

        for (var sample = 0; sample < 30; sample++)
        {
            var allocation = estimator.Apply(baseline, healthy);
            estimator.RecordChunkScheduled(transferId, deliveredPerSample, allocation.MaximumBulkKbps);
            time.Advance(TimeSpan.FromSeconds(
                deliveredPerSample * 8d / (allocation.MaximumBulkKbps * 1000d)));
            delivered += deliveredPerSample;
            Assert.True(estimator.ObserveReceipt(
                transferId,
                delivered,
                baseline.MaximumBulkKbps,
                healthy));
        }

        var learned = estimator.Apply(baseline, healthy);
        Assert.True(learned.MaximumBulkKbps > 150_000);
        Assert.True(estimator.Snapshot.GoodputKbps > 0);
        Assert.Equal(30, estimator.Snapshot.FeedbackSamples);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(baseline.MaximumBulkKbps, estimator.Apply(baseline, healthy).MaximumBulkKbps);

        var pressured = healthy with
        {
            InputToInjectionLatencyP95Ms = 50,
            InputClockUncertaintyMs = 1,
        };
        Assert.Equal(baseline.MaximumBulkKbps, estimator.Apply(baseline, pressured).MaximumBulkKbps);
        Assert.True(estimator.Apply(baseline, healthy).MaximumBulkKbps < learned.MaximumBulkKbps);
    }

    [Fact]
    public void Native_bulk_capacity_ignores_non_monotonic_or_unearned_receipts()
    {
        var time = new ManualTimestampTimeProvider();
        var estimator = new NativeBulkCapacityEstimator(time);
        var transferId = Guid.NewGuid();
        var statistics = new MediaStatistics { ConnectionHealth = ConnectionHealth.Excellent };

        estimator.RecordChunkScheduled(transferId, 256 * 1024, 50_000);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.False(estimator.ObserveReceipt(transferId, 512 * 1024, 50_000, statistics));
        Assert.False(estimator.ObserveReceipt(transferId, -1, 50_000, statistics));
        Assert.Equal(0, estimator.Snapshot.FeedbackSamples);
    }

    [Fact]
    public async Task Dedicated_web_rtc_bulk_uses_receiver_confirmed_delivery_feedback()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer WebRTC bulk");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer WebRTC bulk");
        var sessionId = SessionId.New();
        var statistics = new MediaStatistics
        {
            ConnectionPath = ConnectionPath.DirectLan,
            ConnectionHealth = ConnectionHealth.Excellent,
            FramesEncoded = 42,
            CurrentBitrateKbps = 36_000,
            TargetBitrateKbps = 36_000,
            AvailableOutgoingBitrateKbps = 43_200,
        };
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            IsBulkDataLaneNegotiated = true,
            IsBulkDataLaneReady = true,
            Statistics = statistics,
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            IsBulkDataLaneNegotiated = true,
            IsBulkDataLaneReady = true,
            Statistics = statistics,
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var permissions = SessionPermission.ViewScreen | SessionPermission.FileTransfer;
        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            permissions,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            permissions,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunkReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) =>
        {
            if (message is TransferOffer) offered.TrySetResult();
            if (message is TransferChunk) chunkReceived.TrySetResult();
        };

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((!viewer.IsReady || !sharer.IsReady) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
        Assert.False(viewer.IsNativeBulkTransportReady);

        var transferId = Guid.NewGuid();
        await viewer.SendAsync(new TransferOffer
        {
            TransferId = transferId,
            DisplayName = "dedicated-webrtc.bin",
            TotalBytes = CollaborationProtocolCodec.MaximumChunkBytes,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "dedicated-webrtc.bin",
                    Kind = TransferItemKind.File,
                    Size = CollaborationProtocolCodec.MaximumChunkBytes,
                },
            ],
        });
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var payload = new byte[CollaborationProtocolCodec.MaximumChunkBytes];
        await viewer.SendAsync(new TransferChunk
        {
            TransferId = transferId,
            RelativePath = "dedicated-webrtc.bin",
            Offset = 0,
            Index = 0,
            ChunkSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
            Payload = payload,
        });
        await chunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(10);
        await sharer.ReportFileDeliveryAsync(transferId, payload.Length, flush: true);
        deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (viewer.AdaptiveBulkFeedbackSamples == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Contains(DataMessagePriority.Bulk, sharerMedia.SentDataPriorities);
        Assert.Equal(1, viewer.AdaptiveBulkFeedbackSamples);
        Assert.Equal(0, viewer.NativeBulkFeedbackSamples);
    }

    [Fact]
    public async Task Negotiated_same_lan_native_bulk_waits_for_startup_and_beats_file_relay()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer native bulk");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer native bulk");
        var sessionId = SessionId.New();
        var statistics = new MediaStatistics
        {
            ConnectionPath = ConnectionPath.DirectLan,
            ConnectionHealth = ConnectionHealth.Excellent,
            FramesEncoded = 42,
            CurrentBitrateKbps = 4_000,
            TargetBitrateKbps = 5_000,
        };
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            IsNativeBulkTransportNegotiated = true,
            SelectedRemoteAddress = IPAddress.Loopback,
            Statistics = statistics,
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            IsNativeBulkTransportNegotiated = true,
            SelectedRemoteAddress = IPAddress.Loopback,
            Statistics = statistics,
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var hub = new InMemoryNativeBulkHub();
        var viewerNative = new GatedNativeBulkTransportFactory(hub);
        var viewerRelay = new InMemoryFileRelay();
        var sharerRelay = new InMemoryFileRelay();
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;
        var permissions = SessionPermission.ViewScreen
                          | SessionPermission.ControlInput
                          | SessionPermission.FileTransfer;
        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            permissions,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay,
            nativeBulkTransportFactory: viewerNative);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            permissions,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay,
            nativeBulkTransportFactory: new InMemoryNativeBulkTransportFactory(hub));

        var fileReceived = new TaskCompletionSource<TransferOffer>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inputReceived = new TaskCompletionSource<RemoteInputFocusRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerApplicationMessages = 0;
        viewer.MessageReceived += (_, _) => Interlocked.Increment(ref viewerApplicationMessages);
        sharer.MessageReceived += (_, message) =>
        {
            if (message is TransferOffer offer) fileReceived.TrySetResult(offer);
            if (message is RemoteInputFocusRequest input) inputReceived.TrySetResult(input);
        };

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((!viewer.IsReady || !sharer.IsReady) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
        await viewerNative.CreationRequested.WaitAsync(TimeSpan.FromSeconds(5));

        var mediaMessagesBeforeNativeStartup = viewerMedia.SentData.Count;
        var transferId = Guid.NewGuid();
        var offerSend = viewer.SendAsync(new TransferOffer
        {
            TransferId = transferId,
            DisplayName = "native.bin",
            TotalBytes = 1024 * 1024,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "native.bin",
                    Kind = TransferItemKind.File,
                    Size = 1024 * 1024,
                },
            ],
        });
        Assert.False(offerSend.IsCompleted);
        viewerNative.Release();
        await offerSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(transferId, (await fileReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).TransferId);
        Assert.Equal(1, hub.Connections);
        Assert.True(viewer.IsNativeBulkTransportReady);
        Assert.True(sharer.IsNativeBulkTransportReady);
        Assert.Equal(1, hub.MessagesSent);
        Assert.Equal(mediaMessagesBeforeNativeStartup + 1, viewerMedia.SentData.Count);
        Assert.Empty(viewerRelay.Sent);
        var mediaFramesBeforeFile = viewerMedia.SentData.Count;

        var chunkPayload = new byte[CollaborationProtocolCodec.MaximumChunkBytes];
        var chunkHash = Convert.ToHexStringLower(SHA256.HashData(chunkPayload));
        for (var index = 0; index < 4; index++)
        {
            await viewer.SendAsync(new TransferChunk
            {
                TransferId = transferId,
                RelativePath = "native.bin",
                Offset = index * (long)chunkPayload.Length,
                Index = index,
                ChunkSha256 = chunkHash,
                Payload = chunkPayload,
            });
        }
        await Task.Delay(100);
        await sharer.ReportFileDeliveryAsync(transferId, 1024 * 1024, flush: true);
        deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (viewer.NativeBulkFeedbackSamples == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, viewer.NativeBulkFeedbackSamples);
        Assert.True(viewer.NativeBulkGoodputKbps > 0);
        Assert.True(viewer.NativeBulkBudgetKbps > 0);
        Assert.Equal(0, viewerApplicationMessages);
        Assert.Equal(6, hub.MessagesSent);
        Assert.Equal(mediaFramesBeforeFile, viewerMedia.SentData.Count);

        await viewer.SendAsync(new RemoteInputFocusRequest
        {
            InputVersion = 1,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
            Enabled = true,
        });
        Assert.True((await inputReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Enabled);
        Assert.Equal(6, hub.MessagesSent);
        Assert.Equal(mediaFramesBeforeFile + 1, viewerMedia.SentData.Count);

        viewer.CompleteTransfer(transferId);
        sharer.CompleteTransfer(transferId);
        viewerMedia.Statistics = statistics with { ConnectionPath = ConnectionPath.DirectInternet };
        sharerMedia.Statistics = statistics with { ConnectionPath = ConnectionPath.DirectInternet };
        var fallbackTransferId = Guid.NewGuid();
        var fallbackReceived = new TaskCompletionSource<TransferOffer>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) =>
        {
            if (message is TransferOffer { TransferId: var id } offer && id == fallbackTransferId)
                fallbackReceived.TrySetResult(offer);
        };
        await viewer.SendAsync(new TransferOffer
        {
            TransferId = fallbackTransferId,
            DisplayName = "fallback.bin",
            TotalBytes = 1,
            Entries =
            [
                new TransferEntry
                {
                    RelativePath = "fallback.bin",
                    Kind = TransferItemKind.File,
                    Size = 1,
                },
            ],
        });

        Assert.Equal(
            fallbackTransferId,
            (await fallbackReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).TransferId);
        Assert.Equal(6, hub.MessagesSent);
        Assert.Equal(mediaFramesBeforeFile + 1, viewerMedia.SentData.Count);
        Assert.Single(viewerRelay.Sent);
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Negotiated_peer_clock_sync_is_encrypted_and_not_dispatched_as_application_data(
        bool videoFrameTelemetryNegotiated,
        bool inputAcknowledgementNegotiated)
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            IsVideoFrameTelemetryNegotiated = videoFrameTelemetryNegotiated,
            IsInputAcknowledgementNegotiated = inputAcknowledgementNegotiated,
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            IsVideoFrameTelemetryNegotiated = videoFrameTelemetryNegotiated,
            IsInputAcknowledgementNegotiated = inputAcknowledgementNegotiated,
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerClock = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var sharerClock = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1).AddMilliseconds(50));

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            timeProvider: viewerClock);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            timeProvider: sharerClock);
        var applicationMessages = 0;
        viewer.MessageReceived += (_, _) => Interlocked.Increment(ref applicationMessages);
        sharer.MessageReceived += (_, _) => Interlocked.Increment(ref applicationMessages);

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (viewerMedia.PeerClockEstimate is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        var estimate = Assert.IsType<PeerClockEstimate>(viewerMedia.PeerClockEstimate);
        Assert.Equal(50_000, estimate.RemoteMinusLocalOffsetMicroseconds);
        Assert.Equal(0, estimate.UncertaintyMicroseconds);
        Assert.Equal(0, Volatile.Read(ref applicationMessages));
        Assert.Contains(viewerMedia.SentData, frame => frame.AsSpan().StartsWith("PNQE"u8));
        Assert.Contains(sharerMedia.SentData, frame => frame.AsSpan().StartsWith("PNQE"u8));
        Assert.DoesNotContain(viewerMedia.SentData, frame => frame.AsSpan().StartsWith("PNQT"u8));
        Assert.DoesNotContain(sharerMedia.SentData, frame => frame.AsSpan().StartsWith("PNQT"u8));
    }

    [Fact]
    public void Managed_post_quantum_provider_round_trips_mldsa_with_context()
    {
        var privateKey = PostQuantumCryptography.GenerateMlDsa65PrivateKey();
        try
        {
            var publicKey = PostQuantumCryptography.ExportMlDsa65PublicKey(privateKey);
            var data = "PeerOnQ transcript"u8.ToArray();
            var context = SecureSessionProtocol.ClientKeySignatureContext;
            var signature = PostQuantumCryptography.SignMlDsa65(
                privateKey,
                data,
                context);

            Assert.Equal(PostQuantumCryptography.MlDsa65PublicKeyBytes, publicKey.Length);
            Assert.Equal(PostQuantumCryptography.MlDsa65SignatureBytes, signature.Length);
            Assert.True(PostQuantumCryptography.VerifyMlDsa65(
                publicKey,
                data,
                signature,
                context));

            data[^1] ^= 0x01;
            Assert.False(PostQuantumCryptography.VerifyMlDsa65(
                publicKey,
                data,
                signature,
                context));
            Assert.False(PostQuantumCryptography.VerifyMlDsa65(
                publicKey,
                "PeerOnQ transcript"u8,
                signature,
                SecureSessionProtocol.ServerHelloSignatureContext));
            CryptographicOperations.ZeroMemory(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [Fact]
    public void Managed_post_quantum_provider_round_trips_mlkem_and_rejects_tampering()
    {
        using var privateKey = PostQuantumCryptography.CreateMlKem768PrivateKey();
        var encapsulation = PostQuantumCryptography.EncapsulateMlKem768(
            privateKey.ExportEncapsulationKey());
        var decapsulated = privateKey.Decapsulate(encapsulation.Ciphertext);
        try
        {
            Assert.Equal(
                PostQuantumCryptography.MlKem768CiphertextBytes,
                encapsulation.Ciphertext.Length);
            Assert.Equal(
                PostQuantumCryptography.MlKem768SharedSecretBytes,
                encapsulation.SharedSecret.Length);
            Assert.Equal(encapsulation.SharedSecret, decapsulated);

            encapsulation.Ciphertext[^1] ^= 0x01;
            var tamperedSecret = privateKey.Decapsulate(encapsulation.Ciphertext);
            try
            {
                Assert.NotEqual(encapsulation.SharedSecret, tamperedSecret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tamperedSecret);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encapsulation.SharedSecret);
            CryptographicOperations.ZeroMemory(decapsulated);
        }
    }

#pragma warning disable SYSLIB5006 // Compatibility test for existing platform-generated device keys.
    [Fact]
    public void Managed_mlkem_provider_is_compatible_with_platform_keys_when_available()
    {
        if (!MLKem.IsSupported) return;

        using var platformPrivateKey = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
        var managedEncapsulation = PostQuantumCryptography.EncapsulateMlKem768(
            platformPrivateKey.ExportEncapsulationKey());
        var platformSecret = platformPrivateKey.Decapsulate(managedEncapsulation.Ciphertext);
        try
        {
            Assert.Equal(managedEncapsulation.SharedSecret, platformSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(managedEncapsulation.SharedSecret);
            CryptographicOperations.ZeroMemory(platformSecret);
        }

        using var managedPrivateKey = PostQuantumCryptography.CreateMlKem768PrivateKey();
        using var platformEncapsulator = MLKem.ImportEncapsulationKey(
            MLKemAlgorithm.MLKem768,
            managedPrivateKey.ExportEncapsulationKey());
        platformEncapsulator.Encapsulate(out var platformCiphertext, out var expectedSecret);
        var managedSecret = managedPrivateKey.Decapsulate(platformCiphertext);
        try
        {
            Assert.Equal(expectedSecret, managedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSecret);
            CryptographicOperations.ZeroMemory(managedSecret);
        }
    }

    [Fact]
    public void Managed_mldsa_provider_is_compatible_with_platform_keys_when_available()
    {
        if (!MLDsa.IsSupported) return;

        var data = "PeerOnQ provider compatibility"u8.ToArray();
        var context = SecureSessionProtocol.ServerHelloSignatureContext;
        using var platformKey = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var platformPrivateKey = platformKey.ExportPkcs8PrivateKey();
        try
        {
            var managedPublicKey = PostQuantumCryptography.ExportMlDsa65PublicKey(
                platformPrivateKey);
            var managedSignature = PostQuantumCryptography.SignMlDsa65(
                platformPrivateKey,
                data,
                context);
            try
            {
                using var platformVerifier = MLDsa.ImportMLDsaPublicKey(
                    MLDsaAlgorithm.MLDsa65,
                    managedPublicKey);
                Assert.True(platformVerifier.VerifyData(data, managedSignature, context));

                var platformSignature = platformKey.SignData(data, context);
                try
                {
                    Assert.True(PostQuantumCryptography.VerifyMlDsa65(
                        managedPublicKey,
                        data,
                        platformSignature,
                        context));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(platformSignature);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(managedSignature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(platformPrivateKey);
        }

        var managedPrivateKey = PostQuantumCryptography.GenerateMlDsa65PrivateKey();
        try
        {
            using var importedPlatformKey = MLDsa.ImportPkcs8PrivateKey(managedPrivateKey);
            Assert.Equal(MLDsaAlgorithm.MLDsa65, importedPlatformKey.Algorithm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(managedPrivateKey);
        }
    }
#pragma warning restore SYSLIB5006

    [Fact]
    public void Handshake_codec_rejects_downgrade_and_truncated_frames()
    {
        var frame = SecureHandshakeCodec.Encode(new SecureClientHello
        {
            SessionId = Guid.NewGuid(),
            Nonce = new byte[32],
            X25519PublicKey = new byte[32],
            Identity = new HybridIdentityPublic
            {
                LegacyP256PublicKeySpki = [1],
                Ed25519PublicKey = new byte[32],
                MLDsa65PublicKey = [1],
                LegacyBindingSignature = [1],
            },
        });
        var downgraded = frame.ToArray();
        downgraded[4] = 0;
        Assert.False(SecureHandshakeCodec.TryDecode(downgraded, out _, out var downgradeError));
        Assert.Equal("secure_protocol_downgrade", downgradeError);
        Assert.False(SecureHandshakeCodec.TryDecode(frame.AsSpan(0, frame.Length - 1), out _, out var lengthError));
        Assert.Equal("invalid_handshake_length", lengthError);
    }

    [Fact]
    public void Handshake_validation_rejects_invalid_classical_and_post_quantum_key_material()
    {
        var sessionId = Guid.NewGuid();
        var identity = new HybridIdentityPublic
        {
            LegacyP256PublicKeySpki = [1],
            Ed25519PublicKey = new byte[32],
            MLDsa65PublicKey = [1],
            LegacyBindingSignature = [1],
        };
        var invalidClassical = new SecureClientHello
        {
            SessionId = sessionId,
            Nonce = new byte[32],
            X25519PublicKey = new byte[31],
            Identity = identity,
        };
        Assert.Throws<CryptographicException>(() =>
            SecureHandshakeCodec.ValidateHello(invalidClassical, sessionId));

        var invalidPostQuantum = new SecureServerHello
        {
            SessionId = sessionId,
            Nonce = new byte[32],
            X25519PublicKey = new byte[32],
            MlKem768PublicKey = [1],
            Identity = identity,
            Ed25519Signature = [1],
            MLDsa65Signature = [1],
        };
        Assert.Throws<CryptographicException>(() =>
            SecureHandshakeCodec.ValidateHello(invalidPostQuantum, sessionId));
    }

    [Fact]
    public async Task Hybrid_identity_rejects_signature_and_transcript_modification()
    {
        using var device = await TestHybridIdentity.CreateAsync("Device");
        var identity = await device.Service.GetPublicIdentityAsync();
        var transcript = RandomNumberGenerator.GetBytes(64);
        var context = SecureSessionProtocol.ClientKeySignatureContext;
        var signature = await device.Service.SignTranscriptAsync(transcript, context);

        Assert.Equal(
            identity.Fingerprint,
            HybridIdentityVerifier.VerifyAndFingerprint(
                identity, device.LegacyFingerprint, transcript, context, signature));

        var badEd = signature.Ed25519Signature.ToArray();
        badEd[0] ^= 0x80;
        Assert.Throws<CryptographicException>(() => HybridIdentityVerifier.VerifyAndFingerprint(
            identity,
            device.LegacyFingerprint,
            transcript,
            context,
            signature with { Ed25519Signature = badEd }));

        var badPostQuantum = signature.MLDsa65Signature.ToArray();
        badPostQuantum[^1] ^= 0x20;
        Assert.Throws<CryptographicException>(() => HybridIdentityVerifier.VerifyAndFingerprint(
            identity,
            device.LegacyFingerprint,
            transcript,
            context,
            signature with { MLDsa65Signature = badPostQuantum }));

        var alteredTranscript = transcript.ToArray();
        alteredTranscript[^1] ^= 0x40;
        Assert.Throws<CryptographicException>(() => HybridIdentityVerifier.VerifyAndFingerprint(
            identity,
            device.LegacyFingerprint,
            alteredTranscript,
            context,
            signature));
    }

    [Fact]
    public async Task Hybrid_handshake_authenticates_both_peers_before_encrypted_application_data()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var permissions = SessionPermission.ViewScreen |
                          SessionPermission.ControlInput |
                          SessionPermission.FileTransfer;
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.DirectLan },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.DirectLan },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            permissions,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            permissions,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
        Assert.True(viewer.Security?.PostQuantumProtected);
        Assert.True(sharer.Security?.PeerAuthenticated);

        var received = new TaskCompletionSource<CollaborationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) => received.TrySetResult(message);
        await viewer.SendAsync(new RemoteInputReleaseAll
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
        });

        var message = Assert.IsType<RemoteInputReleaseAll>(
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(sessionId.Value, message.SessionId);
        Assert.Equal("PNQE"u8.ToArray(), viewerMedia.SentData[^1].AsSpan(0, 4).ToArray());
        Assert.Equal(-1, viewerMedia.SentData[^1].AsSpan().IndexOf("input.releaseAll"u8));

        viewerMedia.Statistics = viewerMedia.Statistics with { ConnectionPath = ConnectionPath.Relayed };
        var directOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewer.SendAsync(new TransferPause { TransferId = Guid.NewGuid() }));
        Assert.Equal("direct_p2p_required", directOnly.Message);
    }

    [Fact]
    public async Task Negotiated_file_relay_moves_file_records_in_both_directions_without_a_direct_ice_path()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerRelay = new InMemoryFileRelay();
        var sharerRelay = new InMemoryFileRelay();
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.FileTransfer,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.FileTransfer,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var receivedBySharer = new TaskCompletionSource<CollaborationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedByViewer = new TaskCompletionSource<CollaborationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) => receivedBySharer.TrySetResult(message);
        viewer.MessageReceived += (_, message) => receivedByViewer.TrySetResult(message);
        var viewerDirectFrames = viewerMedia.SentData.Count;
        var sharerDirectFrames = sharerMedia.SentData.Count;

        await viewer.SendAsync(new TransferPause { TransferId = Guid.NewGuid() });
        await sharer.SendAsync(new TransferResume { TransferId = Guid.NewGuid() });

        Assert.IsType<TransferPause>(await receivedBySharer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TransferResume>(await receivedByViewer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(viewerDirectFrames, viewerMedia.SentData.Count);
        Assert.Equal(sharerDirectFrames, sharerMedia.SentData.Count);
        Assert.Single(viewerRelay.Sent);
        Assert.Single(sharerRelay.Sent);
        Assert.All(viewerRelay.Sent.Concat(sharerRelay.Sent), frame =>
            Assert.Equal("PNQE"u8.ToArray(), frame.AsSpan(0, 4).ToArray()));
    }

    [Fact]
    public async Task Secure_file_records_are_serialized_before_relay_without_media_rate_pacing()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerRelay = new ControllableFileRelay(blockFirstSend: true);
        var sharerRelay = new ControllableFileRelay(blockFirstSend: false);
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.ViewScreen | SessionPermission.FileTransfer | SessionPermission.ControlInput,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.ViewScreen | SessionPermission.FileTransfer | SessionPermission.ControlInput,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var transferId = Guid.NewGuid();
        var received = new List<Type>();
        var receivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputCount = 0;
        var protocolError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) =>
        {
            if (message is RemoteInputReleaseAll)
            {
                if (Interlocked.Increment(ref inputCount) == 1)
                    inputReceived.TrySetResult();
                else
                    secondInputReceived.TrySetResult();
                return;
            }
            received.Add(message.GetType());
            if (received.Count == 2) receivedBoth.TrySetResult();
        };
        sharer.ProtocolError += (_, reason) => protocolError.TrySetResult(reason);

        var firstSend = viewer.SendAsync(new TransferPause { TransferId = transferId });
        await viewerRelay.FirstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewer.SendAsync(new RemoteInputReleaseAll
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 1,
        }).WaitAsync(TimeSpan.FromSeconds(1));
        await inputReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondSend = viewer.SendAsync(new TransferResume { TransferId = transferId });
        var secondEnteredBeforeRelease = false;
        try
        {
            await viewerRelay.SecondSendEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            secondEnteredBeforeRelease = true;
        }
        catch (TimeoutException)
        {
            // The second record must remain behind the first protected record.
        }
        finally
        {
            viewerRelay.ReleaseFirstSend.TrySetResult();
        }

        await Task.WhenAll(firstSend, secondSend);
        Assert.False(secondEnteredBeforeRelease);
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([typeof(TransferPause), typeof(TransferResume)], received);
        Assert.False(protocolError.Task.IsCompleted);
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);

        viewerMedia.Statistics = new MediaStatistics
        {
            ConnectionPath = ConnectionPath.Relayed,
            ConnectionHealth = ConnectionHealth.Poor,
            FramesRendered = 1,
            CurrentBitrateKbps = 34_000,
            TargetBitrateKbps = 36_000,
            AvailableOutgoingBitrateKbps = 36_000,
        };
        var payload = new byte[16 * 1024];
        RandomNumberGenerator.Fill(payload);
        TransferChunk Chunk(long index) => new()
        {
            TransferId = transferId,
            RelativePath = "relay.bin",
            Offset = index * payload.Length,
            Index = index,
            ChunkSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        };

        var sendsBeforeTransfer = viewerRelay.SendCount;
        await viewer.SendAsync(Chunk(0));
        await viewer.SendAsync(Chunk(1)).WaitAsync(TimeSpan.FromMilliseconds(100));
        Assert.Equal(sendsBeforeTransfer + 2, viewerRelay.SendCount);

        await viewer.SendAsync(new RemoteInputReleaseAll
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionGeneration = 1,
            FocusGeneration = 1,
            Sequence = 2,
        }).WaitAsync(TimeSpan.FromSeconds(1));
        await secondInputReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewer.SendAsync(new TransferResume { TransferId = transferId });
        Assert.False(protocolError.Task.IsCompleted);
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
    }

    [Fact]
    public async Task File_relay_completion_dispatch_does_not_block_direct_remote_input()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerRelay = new InMemoryFileRelay();
        var sharerRelay = new InMemoryFileRelay();
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;
        var permissions = SessionPermission.ViewScreen |
                          SessionPermission.ControlInput |
                          SessionPermission.FileTransfer;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            permissions,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            permissions,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var fileCompletionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFileCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocolError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) =>
        {
            if (message is TransferComplete)
            {
                fileCompletionEntered.TrySetResult();
                releaseFileCompletion.Task.GetAwaiter().GetResult();
            }
            else if (message is RemoteInputReleaseAll)
            {
                inputReceived.TrySetResult();
            }
        };
        sharer.ProtocolError += (_, reason) => protocolError.TrySetResult(reason);

        var completionSend = Task.Run(() => viewer.SendAsync(new TransferComplete
        {
            TransferId = Guid.NewGuid(),
        }));
        await fileCompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await viewer.SendAsync(new RemoteInputReleaseAll
            {
                InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
                SessionGeneration = 1,
                FocusGeneration = 1,
                Sequence = 1,
            });
            await inputReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(completionSend.IsCompleted);
        }
        finally
        {
            releaseFileCompletion.TrySetResult();
        }

        await completionSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(protocolError.Task.IsCompleted);
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
    }

    [Fact]
    public async Task Encrypted_relay_file_transfer_completes_on_both_ends_without_ending_the_screen_session()
    {
        using var sourceRoot = new SecureTransportTestDirectory();
        using var destinationRoot = new SecureTransportTestDirectory();
        var source = Path.Combine(sourceRoot.Path, "payload.bin");
        var expected = Enumerable.Range(0, 600_000).Select(index => (byte)(index % 239)).ToArray();
        await File.WriteAllBytesAsync(source, expected);

        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerRelay = new InMemoryFileRelay();
        var sharerRelay = new InMemoryFileRelay();
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.FileTransfer,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.FileTransfer,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await using var sender = new FileTransferService(viewer);
        await using var receiver = new FileTransferService(sharer);
        var offered = new TaskCompletionSource<TransferOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocolError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.IncomingOffer += (_, offer) => offered.TrySetResult(offer);
        sender.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) senderCompleted.TrySetResult();
        };
        receiver.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.Status == TransferStatus.Completed) receiverCompleted.TrySetResult();
        };
        viewer.ProtocolError += (_, reason) => protocolError.TrySetResult(reason);
        sharer.ProtocolError += (_, reason) => protocolError.TrySetResult(reason);

        await sender.OfferAsync([source]);
        var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.AcceptAsync(offer.TransferId, destinationRoot.Path, TransferCollisionPolicy.Rename);
        await Task.WhenAll(
            senderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15)),
            receiverCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "payload.bin")));
        Assert.True(viewer.IsReady);
        Assert.True(sharer.IsReady);
        Assert.False(protocolError.Task.IsCompleted);
    }

    [Fact]
    public async Task File_relay_applies_receiver_backpressure_before_reading_the_next_record()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer)
        {
            Statistics = new MediaStatistics { ConnectionPath = ConnectionPath.Relayed },
        };
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        var viewerRelay = new InMemoryFileRelay();
        var sharerRelay = new InMemoryFileRelay();
        viewerRelay.Peer = sharerRelay;
        sharerRelay.Peer = viewerRelay;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.FileTransfer,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint,
            fileRelay: viewerRelay);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.FileTransfer,
            sharerIdentity.Service,
            viewerIdentity.LegacyFingerprint,
            fileRelay: sharerRelay);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => viewerReady.TrySetResult();
        sharer.Ready += (_, _) => sharerReady.TrySetResult();
        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await Task.WhenAll(
            viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            sharerReady.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, _) =>
        {
            received.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var payload = "backpressure"u8.ToArray();
        var send = Task.Run(() => viewer.SendAsync(new TransferChunk
        {
            TransferId = Guid.NewGuid(),
            RelativePath = "payload.bin",
            Offset = 0,
            Index = 0,
            ChunkSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            Payload = payload,
        }));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(send.IsCompleted);

        release.TrySetResult();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task First_secure_record_is_accepted_when_viewer_sends_it_from_ready_callback()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var permissions = SessionPermission.ViewScreen | SessionPermission.ControlInput;
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer);
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer);
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;

        await using var viewer = new MediaCollaborationTransport(
            viewerMedia, permissions, viewerIdentity.Service, sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia, permissions, sharerIdentity.Service, viewerIdentity.LegacyFingerprint);
        var received = new TaskCompletionSource<CollaborationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sendFailed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.MessageReceived += (_, message) => received.TrySetResult(message);
        viewer.Ready += (_, _) => _ = SendFirstRecordAsync();

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();

        var completed = await Task.WhenAny(received.Task, sendFailed.Task)
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (completed == sendFailed.Task)
            throw await sendFailed.Task;
        Assert.IsType<RemoteInputReleaseAll>(await received.Task);
        Assert.True(sharer.IsReady);

        async Task SendFirstRecordAsync()
        {
            try
            {
                await viewer.SendAsync(new RemoteInputReleaseAll
                {
                    InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
                    SessionGeneration = 1,
                    FocusGeneration = 1,
                    Sequence = 1,
                });
            }
            catch (Exception ex)
            {
                sendFailed.TrySetResult(ex);
            }
        }
    }

    [Fact]
    public async Task Established_transport_rejects_a_replayed_handshake_frame()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer);
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer);
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        await using var viewer = new MediaCollaborationTransport(
            viewerMedia, SessionPermission.ViewScreen, viewerIdentity.Service, sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia, SessionPermission.ViewScreen, sharerIdentity.Service, viewerIdentity.LegacyFingerprint);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ready += (_, _) => ready.TrySetResult();
        sharer.ProtocolError += (_, reason) => rejected.TrySetResult(reason);

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var clientHello = viewerMedia.SentData[0];
        sharerMedia.EmitData(clientHello);

        Assert.Equal("unexpected_handshake_message", await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(sharer.IsReady);
    }

    [Fact]
    public async Task Hybrid_handshake_fails_closed_when_signaling_identity_does_not_match()
    {
        using var viewerIdentity = await TestHybridIdentity.CreateAsync("Viewer");
        using var sharerIdentity = await TestHybridIdentity.CreateAsync("Sharer");
        var sessionId = SessionId.New();
        var viewerMedia = new FakeMediaSession(sessionId, SessionRole.Viewer);
        var sharerMedia = new FakeMediaSession(sessionId, SessionRole.Sharer);
        viewerMedia.DataPeer = sharerMedia;
        sharerMedia.DataPeer = viewerMedia;
        await using var viewer = new MediaCollaborationTransport(
            viewerMedia,
            SessionPermission.ViewScreen,
            viewerIdentity.Service,
            sharerIdentity.LegacyFingerprint);
        await using var sharer = new MediaCollaborationTransport(
            sharerMedia,
            SessionPermission.ViewScreen,
            sharerIdentity.Service,
            new string('f', 64));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.ProtocolError += (_, reason) => failed.TrySetResult(reason);

        sharerMedia.SetDataChannelReady();
        viewerMedia.SetDataChannelReady();

        Assert.Equal("secure_protocol_error", await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(viewer.IsReady);
        Assert.False(sharer.IsReady);
    }

    [Fact]
    public void Record_layer_rejects_tampering_replay_and_wrong_transfer_context()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var viewer = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        using var sharer = new SessionTrafficProtector(master, sessionId, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);

        var first = viewer.Protect(SecureChannelKind.Input, Guid.Empty, "first"u8);
        Assert.True(sharer.TryUnprotect(
            SecureChannelKind.Input,
            Guid.Empty,
            first,
            out var plaintext,
            out var error), error);
        Assert.Equal("first"u8.ToArray(), plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        Assert.False(sharer.TryUnprotect(
            SecureChannelKind.Input,
            Guid.Empty,
            first,
            out _,
            out var replayError));
        Assert.Equal("replayed_or_out_of_order_record", replayError);

        var second = viewer.Protect(SecureChannelKind.Input, Guid.Empty, "second"u8);
        second[^1] ^= 0x80;
        Assert.False(sharer.TryUnprotect(
            SecureChannelKind.Input,
            Guid.Empty,
            second,
            out _,
            out var tamperError));
        Assert.Equal("authentication_failed", tamperError);

        viewer.ForceRekey();
        var rekeyed = viewer.Protect(SecureChannelKind.Input, Guid.Empty, "new epoch"u8);
        Assert.True(sharer.TryUnprotect(
            SecureChannelKind.Input,
            Guid.Empty,
            rekeyed,
            out var rekeyedPlaintext,
            out var rekeyError), rekeyError);
        CryptographicOperations.ZeroMemory(rekeyedPlaintext);

        var transferId = Guid.NewGuid();
        var fileRecord = viewer.Protect(SecureChannelKind.FileTransfer, transferId, "chunk"u8);
        Assert.False(sharer.TryUnprotect(
            SecureChannelKind.FileTransfer,
            Guid.NewGuid(),
            fileRecord,
            out _,
            out var contextError));
        Assert.Equal("invalid_secure_frame", contextError);
    }

    [Fact]
    public void Record_layer_rejects_ciphertext_tag_and_wrong_session_key()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var wrongMaster = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var sender = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        using var receiver = new SessionTrafficProtector(master, sessionId, SessionRole.Sharer);
        using var wrongReceiver = new SessionTrafficProtector(wrongMaster, sessionId, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);
        CryptographicOperations.ZeroMemory(wrongMaster);

        var ciphertextModified = sender.Protect(SecureChannelKind.Control, Guid.Empty, "ciphertext"u8);
        ciphertextModified[^17] ^= 0x01;
        Assert.False(receiver.TryUnprotect(
            SecureChannelKind.Control, Guid.Empty, ciphertextModified, out _, out var ciphertextError));
        Assert.Equal("authentication_failed", ciphertextError);

        var tagModified = sender.Protect(SecureChannelKind.Input, Guid.Empty, "tag"u8);
        tagModified[^1] ^= 0x01;
        Assert.False(receiver.TryUnprotect(
            SecureChannelKind.Input, Guid.Empty, tagModified, out _, out var tagError));
        Assert.Equal("authentication_failed", tagError);

        var wrongKeyRecord = sender.Protect(SecureChannelKind.Clipboard, Guid.Empty, "wrong key"u8);
        Assert.False(wrongReceiver.TryUnprotect(
            SecureChannelKind.Clipboard, Guid.Empty, wrongKeyRecord, out _, out var keyError));
        Assert.Equal("authentication_failed", keyError);
    }

    [Fact]
    public void Key_schedule_separates_direction_channel_and_transfer_keys()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var viewer = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(master);
        var input = viewer.Protect(SecureChannelKind.Input, Guid.Empty, "same"u8);
        var clipboard = viewer.Protect(SecureChannelKind.Clipboard, Guid.Empty, "same"u8);
        var fileA = viewer.Protect(SecureChannelKind.FileTransfer, Guid.NewGuid(), "same"u8);
        var fileB = viewer.Protect(SecureChannelKind.FileTransfer, Guid.NewGuid(), "same"u8);

        Assert.NotEqual(input, clipboard);
        Assert.NotEqual(fileA, fileB);
    }

    [Fact]
    public void Video_rekey_drops_late_old_epoch_frames_without_accepting_replay()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var viewer = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        using var sharer = new SessionTrafficProtector(master, sessionId, SessionRole.Sharer);
        CryptographicOperations.ZeroMemory(master);

        var firstOld = viewer.ProtectVideoFrame("old-0"u8);
        var lateOld = viewer.ProtectVideoFrame("old-1"u8);
        Assert.True(sharer.TryUnprotectVideoFrame(firstOld, out var firstPlaintext, out var firstError), firstError);
        CryptographicOperations.ZeroMemory(firstPlaintext);

        viewer.ForceRekey();
        var newEpoch = viewer.ProtectVideoFrame("new-0"u8);
        Assert.True(sharer.TryUnprotectVideoFrame(newEpoch, out var newPlaintext, out var newError), newError);
        CryptographicOperations.ZeroMemory(newPlaintext);

        Assert.False(sharer.TryUnprotectVideoFrame(lateOld, out _, out var staleError));
        Assert.Equal("stale_video_record", staleError);
        Assert.False(sharer.TryUnprotectVideoFrame(newEpoch, out _, out var replayError));
        Assert.Equal("stale_video_record", replayError);
    }

    [Fact]
    public void Video_receiver_can_start_from_the_first_authenticated_frame_that_survived_packet_loss()
    {
        var master = RandomNumberGenerator.GetBytes(64);
        var sessionId = SessionId.New();
        using var sharer = new SessionTrafficProtector(master, sessionId, SessionRole.Sharer);
        using var viewer = new SessionTrafficProtector(master, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(master);

        _ = sharer.ProtectVideoFrame("lost-before-receiver-started"u8);
        var firstReceived = sharer.ProtectVideoFrame("first-received"u8);

        Assert.True(
            viewer.TryUnprotectVideoFrame(firstReceived, out var plaintext, out var error),
            error);
        Assert.Equal("first-received"u8.ToArray(), plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
    }
}

internal sealed class SecureTransportTestDirectory : IDisposable
{
    public SecureTransportTestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peeronq-secure-transport-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => 1;
}

internal sealed class ManualTimestampTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public void Advance(TimeSpan value) => Interlocked.Add(ref _timestamp, value.Ticks);
}

internal sealed class InMemoryFileRelay : IFileRelaySignaling
{
    public InMemoryFileRelay? Peer { get; set; }
    public bool IsFileRelayAvailable => true;
    public List<byte[]> Sent { get; } = [];
    public event EventHandler<FileRelayFrame>? FileRelayReceived;

    public Task SendFileRelayAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = payload.ToArray();
        Sent.Add(copy);
        Peer?.Deliver(new FileRelayFrame(sessionId, copy));
        return Task.CompletedTask;
    }

    private void Deliver(FileRelayFrame frame) => FileRelayReceived?.Invoke(this, frame);
}

internal sealed class ControllableFileRelay(bool blockFirstSend) : IFileRelaySignaling
{
    private int _sendCount;

    public ControllableFileRelay? Peer { get; set; }
    public int SendCount => Volatile.Read(ref _sendCount);
    public bool IsFileRelayAvailable => true;
    public TaskCompletionSource FirstSendEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondSendEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstSend { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler<FileRelayFrame>? FileRelayReceived;

    public async Task SendFileRelayAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var sendNumber = Interlocked.Increment(ref _sendCount);
        if (sendNumber == 1)
        {
            FirstSendEntered.TrySetResult();
            if (blockFirstSend)
                await ReleaseFirstSend.Task.WaitAsync(cancellationToken);
        }
        else if (sendNumber == 2)
        {
            SecondSendEntered.TrySetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        Peer?.Deliver(new FileRelayFrame(sessionId, payload.ToArray()));
    }

    private void Deliver(FileRelayFrame frame) => FileRelayReceived?.Invoke(this, frame);
}

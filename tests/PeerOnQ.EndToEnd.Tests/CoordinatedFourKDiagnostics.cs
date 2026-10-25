using System.Diagnostics;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Platform.Windows.Media;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>Bounded, metadata-only observations around the actual production codecs.</summary>
internal sealed class CoordinatedFourKDiagnostics
{
    private readonly Lock _gate = new();
    private readonly List<EncodeObservation> _encodes = new(1024);
    private readonly List<DecodeObservation> _decodes = new(1024);
    private readonly List<(long At, double Milliseconds)> _colors = new(1024);
    private readonly List<PipelineObservation> _pipeline = new(64);
    private int _legacyCalls;

    internal IHardwareH264Encoder Observe(MediaFoundationH264Encoder encoder) => new ObservedEncoder(this, encoder);
    internal IDeferredH264VideoDecoder Observe(MediaFoundationH264Decoder decoder) => new ObservedDecoder(this, decoder);

    internal void ObservePipeline(MediaStatistics sender, MediaStatistics viewer)
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        var gc = GC.GetGCMemoryInfo();
        double pauseMilliseconds = 0;
        foreach (var duration in gc.PauseDurations) pauseMilliseconds += duration.TotalMilliseconds;
        var observation = new PipelineObservation(Stopwatch.GetTimestamp(), sender, viewer,
            ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount, ThreadPool.ThreadCount,
            availableWorkers, availableIo, GC.GetTotalAllocatedBytes(), GC.CollectionCount(0),
            GC.CollectionCount(1), GC.CollectionCount(2), pauseMilliseconds);
        lock (_gate) if (_pipeline.Count < 128) _pipeline.Add(observation);
    }

    internal void Write(ITestOutputHelper output, long start, long end)
    {
        EncodeObservation[] encodes;
        DecodeObservation[] decodes;
        (long At, double Milliseconds)[] colors;
        PipelineObservation[] pipeline;
        lock (_gate)
        {
            encodes = _encodes.Where(item => item.At >= start && item.At <= end).ToArray();
            decodes = _decodes.Where(item => item.At >= start && item.At <= end).ToArray();
            colors = _colors.Where(item => item.At >= start && item.At <= end).ToArray();
            pipeline = _pipeline.Where(item => item.At >= start && item.At <= end).ToArray();
        }
        var successful = encodes.Where(item => item.Succeeded).ToArray();
        output.WriteLine($"Coordinated codec metadata: encoderCalls={encodes.Length}; successful={successful.Length}; "
            + $"totalEncodeMs={Range(successful.Select(item => item.TotalMs))}; "
            + $"formatMs={Range(successful.Select(item => item.FormatMs))}; nativeEncodeMs={Range(successful.Select(item => item.NativeMs))}; "
            + $"encodedBytes={Range(successful.Select(item => (double)item.Bytes))}; "
            + $"nativeDecodeCalls={decodes.Length}; nativeDecodeMs={Range(decodes.Select(item => item.NativeMs))}; "
            + $"decodeCallMs={Range(decodes.Select(item => item.TotalMs))}; colorCalls={colors.Length}; "
            + $"colorCallMs={Range(colors.Select(item => item.Milliseconds))}; legacyDecodeCalls={Volatile.Read(ref _legacyCalls)}");
        foreach (var group in successful.GroupBy(item => (item.Fps, item.Kbps)))
            output.WriteLine($"Coordinated encoder configuration: requestedFps={group.Key.Fps}; requestedKbps={group.Key.Kbps}; "
                + $"samples={group.Count()}; totalMs={Range(group.Select(item => item.TotalMs))}; "
                + $"nativeMs={Range(group.Select(item => item.NativeMs))}; bytes={Range(group.Select(item => (double)item.Bytes))}; "
                + $"first/lastNativePts={group.First().Pts}/{group.Last().Pts}; "
                + $"nativeDurations={string.Join(',', group.Select(item => item.Duration).Distinct())}; "
                + $"initialisationsMax={group.Max(item => item.Initialisations)}");
        PipelineObservation? previous = null;
        foreach (var sample in pipeline)
        {
            var sender = sample.Sender;
            var viewer = sample.Viewer;
            output.WriteLine($"Coordinated pipeline second={Stopwatch.GetElapsedTime(start, sample.At).TotalSeconds:F2}; "
                + $"senderFps={sender.TargetFps}; reason={sender.QualityChangeReason}; encoderQueue={sender.EncoderQueueDepth}; "
                + $"captured/encoded/dropped={sender.FramesCaptured}/{sender.FramesEncoded}/{sender.FramesDropped}; "
                + $"senderKbps={sender.CurrentBitrateKbps:F2}; rttMs={sender.RttMs:F2}; jitterMs={sender.JitterMs:F2}; lossPercent={sender.PacketLossPercent:F2}; "
                + $"viewerDecoded/renderedFps={viewer.DecodeFps:F2}/{viewer.RenderFps:F2}; "
                + $"viewerAgeMs={viewer.CaptureToPresentLatencyP95Ms:F2}; clockMs={viewer.FrameAgeClockUncertaintyMs:F2}; "
                + $"decodeToPresentMs={viewer.DecodeToRenderLatencyP95Ms:F2}; presentationPending/replaced={viewer.VideoPresentationQueueDepth}/{viewer.VideoPresentationFramesReplaced}; "
                + $"threadPoolPending/completed/threads/availableWorkers/availableIo={sample.Pending}/{sample.Completed}/{sample.Threads}/{sample.AvailableWorkers}/{sample.AvailableIo}; "
                + $"allocatedDelta={(previous is null ? 0 : sample.Allocated - previous.Allocated)}; "
                + $"GC0/1/2delta={(previous is null ? 0 : sample.Gen0 - previous.Gen0)}/"
                + $"{(previous is null ? 0 : sample.Gen1 - previous.Gen1)}/{(previous is null ? 0 : sample.Gen2 - previous.Gen2)}; "
                + $"lastGcPauseSumMs={sample.PauseMs:F2}");
            previous = sample;
        }

        static string Range(IEnumerable<double> values)
        {
            var data = values.ToArray();
            return data.Length == 0 ? "unavailable" : $"{data.Min():F2}/{data.Average():F2}/{data.Max():F2}";
        }
    }

    private sealed class ObservedEncoder(CoordinatedFourKDiagnostics owner, MediaFoundationH264Encoder encoder)
        : IHardwareH264Encoder
    {
        public string Name => encoder.Name;
        public byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame)
        {
            var began = Stopwatch.GetTimestamp();
            byte[]? encoded = null;
            try { return encoded = encoder.EncodeI420(width, height, frame, bitrateKbps, frameRate, forceKeyFrame); }
            finally
            {
                var item = new EncodeObservation(Stopwatch.GetTimestamp(), encoded is not null,
                    Stopwatch.GetElapsedTime(began).TotalMilliseconds, encoder.LastFormatConversionMilliseconds,
                    encoder.LastNativeEncodeMilliseconds, encoded?.Length ?? 0, frameRate, bitrateKbps,
                    encoder.LastOutputSampleTime, encoder.LastOutputSampleDuration, encoder.InitialisationCount);
                lock (owner._gate) if (owner._encodes.Count < 4096) owner._encodes.Add(item);
            }
        }
        public void Dispose() => encoder.Dispose();
    }

    private sealed class ObservedDecoder(CoordinatedFourKDiagnostics owner, MediaFoundationH264Decoder decoder)
        : IDeferredH264VideoDecoder
    {
        public string Name => decoder.Name;
        public bool HardwareAccelerated => decoder.HardwareAccelerated;
        public DecodedNv12Frame[] DecodeNv12(int width, int height, byte[] frame, bool reset)
        {
            var began = Stopwatch.GetTimestamp();
            var images = decoder.DecodeNv12(width, height, frame, reset);
            var item = new DecodeObservation(Stopwatch.GetTimestamp(),
                Stopwatch.GetElapsedTime(began).TotalMilliseconds, decoder.LastNativeDecodeMilliseconds);
            lock (owner._gate) if (owner._decodes.Count < 4096) owner._decodes.Add(item);
            return images;
        }
        public byte[] ConvertNv12ToBgr(DecodedNv12Frame frame)
        {
            var began = Stopwatch.GetTimestamp();
            var pixels = decoder.ConvertNv12ToBgr(frame);
            var item = (Stopwatch.GetTimestamp(), Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            lock (owner._gate) if (owner._colors.Count < 4096) owner._colors.Add(item);
            return pixels;
        }
        public RemoteVideoFrame[] Decode(int width, int height, byte[] frame, bool reset)
        {
            Interlocked.Increment(ref owner._legacyCalls);
            return decoder.Decode(width, height, frame, reset);
        }
        public void Dispose() => decoder.Dispose();
    }

    private sealed record EncodeObservation(long At, bool Succeeded, double TotalMs, double FormatMs, double NativeMs,
        int Bytes, int Fps, int Kbps, long? Pts, long? Duration, int Initialisations);
    private sealed record DecodeObservation(long At, double TotalMs, double NativeMs);
    private sealed record PipelineObservation(long At, MediaStatistics Sender, MediaStatistics Viewer,
        long Pending, long Completed, int Threads, int AvailableWorkers, int AvailableIo,
        long Allocated, int Gen0, int Gen1, int Gen2, double PauseMs);
}

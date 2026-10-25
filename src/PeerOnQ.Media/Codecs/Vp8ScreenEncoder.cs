using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vpxmd;

namespace PeerOnQ.Media.Codecs;

/// <summary>
/// VP8 encoder configured for low-latency desktop content. The upstream convenience encoder in
/// SIPSorceryMedia.Encoders 10.0.4 applies its requested bitrate before loading libvpx defaults,
/// which resets the value. This narrow wrapper configures rate control after defaults and owns
/// dimension/bitrate changes so every restart begins with a key frame.
///
/// Threading: one dedicated thread owns each libvpx context and makes every native call. Rate,
/// frame-rate and key-frame requests only publish desired values that the encoder thread applies
/// before its next frame, so quality changes, picture requests and capture never wait on libvpx.
/// A native call that does not return within the stall timeout abandons that context (it is
/// never destroyed from another thread) and a fresh single-threaded encoder takes over. On 0.9.87
/// LNK-363 (2C/4T) hung inside libvpx while one shared lock also blocked capture, quality
/// changes, picture requests and session close.
/// </summary>
internal sealed partial class Vp8ScreenEncoder : IDisposable
{
    [DllImport("vpxmd", CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpx_codec_control_")]
    private static extern VpxCodecErrT VpxCodecControlInt(nint codec, int controlId, int value);

    // sizeof(vpx_codec_enc_cfg_t): 384 bytes in libvpx 1.9.0 (the generated binding), 504 in 1.17.0.
    internal const int EncoderConfigurationBytes = 504;
    private const int RealtimeDeadline = 1;
    private const int ClockRate = 90_000;
    private const int ForceKeyFrameFlag = 1;
    private const uint DefaultErrorResilientFlag = 1;
    // Chromium Remote Desktop uses libvpx's fastest VP8 realtime setting for full-screen desktop
    // frames. The libvpx default (0) spends far too much CPU on motion search and starves capture.
    private const int RealtimeCpuUsed = 16;
    private const int DesktopScreenContentMode = 1;
    private const int MaximumVp8Dimension = 16_383;
    // One 8K I420 frame is about 47.5 MiB; bound the pinned frame and native working set.
    private const int MaximumI420FrameBytes = 64 * 1024 * 1024;
    // Loss recovery asks for key frames on demand (PLI, sequence gaps, viewer watchdog); the
    // periodic key frame is only a safety net. Every two seconds (0.9.87) it sent a burst that
    // cost a visible hitch on Wi-Fi.
    internal const int KeyFrameSafetyNetSeconds = 5;
    // A key frame may use at most nine frame budgets (WebRTC's 30 fps screen-share bound), so
    // it does not burst the link and make the following frames late.
    internal const int MaximumIntraBitratePercent = 900;
    // libvpx's internal VP8 threads hung on LNK-363 (i5-5300U, 2C/4T). Small machines and every
    // replacement after a stall encode single-threaded.
    internal const int SingleThreadedProcessorLimit = 4;
    internal const int MaximumAbandonedEncoders = 3;
    internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromMilliseconds(500);
    private const int StallTimeoutReferencePixels = 1920 * 1088;
    private const int EncodeTimingSamples = 120;

    private readonly Func<VpxCodecCtx, VpxCodecEncCfg, VpxCodecErrT> _setConfiguration;
    private readonly Func<int> _getNativeVersion;
    private readonly ILogger _log;
    private readonly TimeSpan _stallTimeout;
    private readonly Action<string>? _beforeNativeCall;
    private readonly int _processorCount;
    // Serializes callers of EncodeI420 only (the encode loop); setters never take it.
    private readonly Lock _callGate = new();
    // Guards only the timing ring buffer, after native calls have returned.
    private readonly Lock _timingGate = new();
    private readonly double[] _encodeTimesMs = new double[EncodeTimingSamples];
    private int _encodeTimingCount;
    private int _encodeTimingNext;

    private NativeEncoder? _native;
    private int _targetKbps;
    private int _targetFps;
    private int _keyFrameRequested = 1;
    private int _singleThreadedFallback;
    private int _abandonedEncoders;
    private int _stoppedLogged;
    private int _disposed;
    private int _initialisationCount;
    private int _reconfigurationCount;
    private long _framesEncoded;

    public Vp8ScreenEncoder(int targetKbps, int targetFps) : this(targetKbps, targetFps, null)
    {
    }

    internal Vp8ScreenEncoder(int targetKbps, int targetFps,
        Func<VpxCodecCtx, VpxCodecEncCfg, VpxCodecErrT>? setConfiguration,
        Func<int>? getNativeVersion = null,
        ILogger? logger = null,
        TimeSpan? stallTimeout = null,
        Action<string>? beforeNativeCall = null,
        int? processorCount = null)
    {
        _targetKbps = ClampBitrate(targetKbps);
        _targetFps = ClampFrameRate(targetFps);
        _setConfiguration = setConfiguration ?? vpx_encoder.VpxCodecEncConfigSet;
        _getNativeVersion = getNativeVersion ?? vpx_codec.VpxCodecVersion;
        _log = logger ?? NullLogger.Instance;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _beforeNativeCall = beforeNativeCall;
        _processorCount = processorCount ?? Environment.ProcessorCount;
    }

    internal int InitialisationCount => Volatile.Read(ref _initialisationCount);
    internal int ReconfigurationCount => Volatile.Read(ref _reconfigurationCount);
    internal long LastSubmittedTimestamp { get; private set; }
    internal uint LastSubmittedDuration { get; private set; }

    /// <summary>Encoders abandoned because a native call did not return.</summary>
    internal int AbandonedEncoders => Volatile.Read(ref _abandonedEncoders);

    /// <summary>The desired bitrate; the encoder thread applies it before its next frame.</summary>
    internal int TargetKbps => Volatile.Read(ref _targetKbps);

    internal int TargetFps => Volatile.Read(ref _targetFps);

    /// <summary>Never waits: the next frame is encoded at this rate.</summary>
    internal void SetTargetKbps(int targetKbps) => Volatile.Write(ref _targetKbps, ClampBitrate(targetKbps));

    /// <summary>Never waits: the next frame is encoded at this frame rate.</summary>
    internal void SetTargetFps(int targetFps) => Volatile.Write(ref _targetFps, ClampFrameRate(targetFps));

    /// <summary>Never waits: the next encoded frame is a key frame.</summary>
    internal void ForceKeyFrame() => Volatile.Write(ref _keyFrameRequested, 1);

    /// <summary>libvpx threads for a new encoder: one on small machines and after a stall.</summary>
    internal static int EncoderThreadsFor(int processorCount, bool singleThreadedFallback) =>
        singleThreadedFallback || processorCount <= SingleThreadedProcessorLimit
            ? 1
            : Math.Clamp(processorCount / 2, 1, 8);

    internal static uint KeyFrameInterval(int fps) => (uint)(fps * KeyFrameSafetyNetSeconds);

    internal byte[] EncodeI420(int width, int height, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (width <= 0 || (width & 1) != 0 || width > MaximumVp8Dimension)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"VP8 frame width must be a positive even value no greater than {MaximumVp8Dimension}.");
        if (height <= 0 || (height & 1) != 0 || height > MaximumVp8Dimension)
            throw new ArgumentOutOfRangeException(
                nameof(height),
                $"VP8 frame height must be a positive even value no greater than {MaximumVp8Dimension}.");

        var requiredLength64 = checked((long)width * height * 3 / 2);
        if (requiredLength64 > MaximumI420FrameBytes)
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                requiredLength64,
                $"An I420 frame cannot exceed {MaximumI420FrameBytes} bytes.");

        var requiredLength = checked((int)requiredLength64);
        if (frame.Length != requiredLength)
            throw new ArgumentException($"I420 frame length must be exactly {requiredLength} bytes.", nameof(frame));

        lock (_callGate)
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _abandonedEncoders) >= MaximumAbandonedEncoders)
            {
                if (Interlocked.Exchange(ref _stoppedLogged, 1) == 0)
                    LogEncoderStopped(_log, MaximumAbandonedEncoders);
                return [];
            }

            var native = Volatile.Read(ref _native);
            if (native is null)
            {
                native = new NativeEncoder(this,
                    EncoderThreadsFor(_processorCount, Volatile.Read(ref _singleThreadedFallback) != 0));
                Volatile.Write(ref _native, native);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    // Dispose ran concurrently: whichever side takes the encoder stops it.
                    if (Interlocked.CompareExchange(ref _native, null, native) == native)
                        native.Shutdown(TimeSpan.Zero);
                    ThrowIfDisposed();
                }
            }

            var timeout = StallTimeoutFor(width, height);
            var started = Stopwatch.GetTimestamp();
            if (!native.TryEncode(width, height, frame, timeout, out var encoded, out var error))
            {
                AbandonStalledEncoder(native, width, height);
                return [];
            }

            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            RecordEncodeTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (encoded.Length > 0) Interlocked.Increment(ref _framesEncoded);
            return encoded;
        }
    }

    /// <summary>
    /// Encode time percentiles, the libvpx thread count and what the encoder is doing now. Safe
    /// from any thread; it never waits for a native call.
    /// </summary>
    internal Vp8EncoderDiagnostics GetDiagnostics()
    {
        double p50 = 0, p95 = 0;
        lock (_timingGate)
        {
            if (_encodeTimingCount > 0)
            {
                var samples = _encodeTimesMs.AsSpan(0, _encodeTimingCount).ToArray();
                Array.Sort(samples);
                p50 = Percentile(samples, 0.50);
                p95 = Percentile(samples, 0.95);
            }
        }

        var native = Volatile.Read(ref _native);
        var abandoned = Volatile.Read(ref _abandonedEncoders);
        string state;
        if (Volatile.Read(ref _disposed) != 0) state = "disposed";
        else if (abandoned >= MaximumAbandonedEncoders) state = $"stopped after {abandoned} stalls";
        else if (native is null) state = abandoned > 0 ? $"restarting after {abandoned} stall(s)" : "not started";
        else if (native.TryGetBusy(out var operation, out var busy))
            state = FormattableString.Invariant($"busy in {operation} for {busy.TotalSeconds:0.0} s");
        else state = abandoned > 0 ? $"idle ({abandoned} stall(s) recovered)" : "idle";
        return new Vp8EncoderDiagnostics(p50, p95, state, native?.Threads ?? 0, abandoned,
            Interlocked.Read(ref _framesEncoded));
    }

    /// <summary>The native call in progress and for how long, when there is one.</summary>
    internal bool TryGetBusy(out string operation, out TimeSpan elapsed)
    {
        if (Volatile.Read(ref _native) is { } native && native.TryGetBusy(out operation, out elapsed)) return true;
        operation = string.Empty;
        elapsed = TimeSpan.Zero;
        return false;
    }

    private TimeSpan StallTimeoutFor(int width, int height)
    {
        // A larger-than-1080p frame gets proportionally more time (4K software tests); the field
        // stall happened at 1080p.
        var scale = Math.Max(1, (int)Math.Ceiling((double)width * height / StallTimeoutReferencePixels));
        return _stallTimeout * scale;
    }

    private void AbandonStalledEncoder(NativeEncoder native, int width, int height)
    {
        native.Abandon();
        Volatile.Write(ref _native, null);
        Volatile.Write(ref _singleThreadedFallback, 1);
        Volatile.Write(ref _keyFrameRequested, 1);
        var abandoned = Interlocked.Increment(ref _abandonedEncoders);
        native.TryGetBusy(out var operation, out var busy);
        LogEncoderStalled(_log, operation, busy.TotalSeconds, width, height, native.Threads,
            TargetKbps, TargetFps, abandoned);
    }

    private void RecordEncodeTime(double milliseconds)
    {
        lock (_timingGate)
        {
            _encodeTimesMs[_encodeTimingNext] = milliseconds;
            _encodeTimingNext = (_encodeTimingNext + 1) % _encodeTimesMs.Length;
            if (_encodeTimingCount < _encodeTimesMs.Length) _encodeTimingCount++;
        }
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    internal static bool IsSupportedEncoderVersion(int version) => EncoderAbiVersionFor(version) != 0;

    /// <summary>
    /// The encoder ABI each reviewed libvpx release checks in vpx_codec_enc_init_ver, or 0. Both
    /// releases share every vpx_codec_enc_cfg_t field the generated binding knows; 1.17.0 appends
    /// rate-control fields (see <see cref="NativeEncoderConfiguration"/>).
    /// </summary>
    internal static int EncoderAbiVersionFor(int version) => version switch
    {
        0x010900 => 23, // libvpx 1.9.0, the prebuilt SIPSorceryMedia.Encoders binary
        0x011100 => 39, // libvpx 1.17.0, built from eng/libvpx/libvpx.json
        _ => 0,
    };

    private static void EnsureSuccess(VpxCodecErrT result, string operation)
    {
        if (result != VpxCodecErrT.VPX_CODEC_OK)
            throw new InvalidOperationException($"Could not {operation}: {vpx_codec.VpxCodecErrToString(result)}.");
    }

    private static int ClampBitrate(int targetKbps) => Math.Clamp(targetKbps, 100, 50_000);

    private static int ClampFrameRate(int targetFps) => Math.Clamp(targetFps, 1, 120);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Never blocks on libvpx: the encoder thread releases its context when it is idle, and an
    /// encoder stuck in a native call is left to release its own context if the call returns.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Interlocked.Exchange(ref _native, null)?.Shutdown(DisposeWait);
    }

    /// <summary>
    /// vpx_codec_enc_cfg_t allocated at the largest reviewed native size. The generated binding
    /// allocates libvpx 1.9.0's 384 bytes, but vpx_codec_enc_config_default of libvpx 1.17.0
    /// writes 504: the same offsets for every bound field plus appended rate-control fields.
    /// Zeroed, owned memory that the base Dispose releases with Marshal.FreeHGlobal.
    /// </summary>
    internal sealed unsafe class NativeEncoderConfiguration : VpxCodecEncCfg
    {
        internal NativeEncoderConfiguration() : base(AllocateZeroed(), false) => __ownsNativeInstance = true;

        private static void* AllocateZeroed()
        {
            var native = (void*)Marshal.AllocHGlobal(EncoderConfigurationBytes);
            NativeMemory.Clear(native, EncoderConfigurationBytes);
            return native;
        }
    }

    /// <summary>
    /// One libvpx context and the only thread that touches it. The caller hands over one frame at
    /// a time and waits at most the stall timeout for the result.
    /// </summary>
    private sealed class NativeEncoder
    {
        private readonly Vp8ScreenEncoder _owner;
        private readonly Thread _thread;
        private readonly SemaphoreSlim _work = new(0, 1);
        private readonly ManualResetEventSlim _done = new(false);
        private int _abandoned;
        private int _shutdown;

        private int _jobWidth;
        private int _jobHeight;
        private byte[]? _jobFrame;
        private byte[] _jobResult = [];
        private Exception? _jobError;

        private string _operation = string.Empty;
        private long _operationStarted;

        private VpxCodecCtx? _context;
        private VpxImage? _image;
        private VpxCodecEncCfg? _configuration;
        private uint _width;
        private uint _height;
        private long _presentationTimestamp;
        private int _durationRemainder;
        private bool _forceKeyFrame = true;
        private int _appliedKbps;
        private int _appliedFps;

        public NativeEncoder(Vp8ScreenEncoder owner, int threads)
        {
            _owner = owner;
            Threads = threads;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "PeerOnQ VP8 encoder",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        public int Threads { get; }

        public bool TryEncode(int width, int height, byte[] frame, TimeSpan timeout,
            out byte[] encoded, out Exception? error)
        {
            _jobWidth = width;
            _jobHeight = height;
            _jobFrame = frame;
            _jobResult = [];
            _jobError = null;
            _done.Reset();
            _work.Release();
            if (!_done.Wait(timeout))
            {
                encoded = [];
                error = null;
                return false;
            }

            encoded = _jobResult;
            error = _jobError;
            _jobFrame = null;
            return true;
        }

        public bool TryGetBusy(out string operation, out TimeSpan elapsed)
        {
            var started = Volatile.Read(ref _operationStarted);
            operation = Volatile.Read(ref _operation);
            if (started == 0)
            {
                elapsed = TimeSpan.Zero;
                return false;
            }

            elapsed = Stopwatch.GetElapsedTime(started);
            return true;
        }

        /// <summary>
        /// The caller gave up on this encoder. A thread still in libvpx releases its own context
        /// if the call returns; a thread that finished meanwhile exits and releases it now.
        /// </summary>
        public void Abandon()
        {
            Volatile.Write(ref _abandoned, 1);
            Shutdown(TimeSpan.Zero);
        }

        public void Shutdown(TimeSpan wait)
        {
            Volatile.Write(ref _shutdown, 1);
            try { _work.Release(); }
            catch (SemaphoreFullException) { /* A frame is already queued; the thread exits after it. */ }
            if (wait > TimeSpan.Zero) _thread.Join(wait);
        }

        private bool Abandoned => Volatile.Read(ref _abandoned) != 0;

        /// <summary>An abandoned encoder must not consume requests or counters of its replacement.</summary>
        private void ThrowIfAbandoned()
        {
            if (Abandoned) throw new OperationCanceledException("The VP8 encoder was abandoned.");
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    _work.Wait();
                    if (Volatile.Read(ref _shutdown) != 0) break;
                    try
                    {
                        _jobResult = Encode(_jobWidth, _jobHeight, _jobFrame!);
                    }
                    catch (Exception ex)
                    {
                        _jobError = ex;
                    }

                    if (Abandoned)
                    {
                        LogAbandonedEncoderReturned(_owner._log, Volatile.Read(ref _operation));
                        break;
                    }

                    _done.Set();
                    if (Volatile.Read(ref _shutdown) != 0) break;
                }
            }
            finally
            {
                // Native state is only ever released by the thread that owns it.
                try { Reset(); }
                catch (Exception ex) { LogEncoderReleaseFailed(_owner._log, ex); }
            }
        }

        private void Begin(string operation)
        {
            Volatile.Write(ref _operation, operation);
            Volatile.Write(ref _operationStarted, Stopwatch.GetTimestamp());
            _owner._beforeNativeCall?.Invoke(operation);
        }

        private void End() => Volatile.Write(ref _operationStarted, 0);

        private byte[] Encode(int width, int height, byte[] frame)
        {
            var kbps = Volatile.Read(ref _owner._targetKbps);
            var fps = Volatile.Read(ref _owner._targetFps);
            if (_context is not null && (_width != (uint)width || _height != (uint)height)) Reset();
            if (_context is not null && (kbps != _appliedKbps || fps != _appliedFps)) ApplyRates(kbps, fps);
            if (_context is null) Initialise((uint)width, (uint)height, kbps, fps);
            ThrowIfAbandoned();

            unsafe
            {
                fixed (byte* framePointer = frame)
                {
                    if (VpxImage.VpxImgWrap(
                            _image!,
                            VpxImgFmt.VPX_IMG_FMT_I420,
                            _width,
                            _height,
                            1,
                            framePointer) is null)
                    {
                        throw new InvalidOperationException("libvpx could not wrap the I420 frame.");
                    }

                    var forceKeyFrame = Interlocked.Exchange(ref _owner._keyFrameRequested, 0) == 1 || _forceKeyFrame;
                    var flags = forceKeyFrame ? ForceKeyFrameFlag : 0;
                    var ticks = ClockRate + _durationRemainder;
                    var duration = (uint)(ticks / _appliedFps);
                    _durationRemainder = ticks % _appliedFps;
                    var timestamp = _presentationTimestamp;
                    _presentationTimestamp = checked(timestamp + duration);
                    VpxCodecErrT result;
                    Begin("vpx_codec_encode");
                    try
                    {
                        result = vpx_encoder.VpxCodecEncode(
                            _context!,
                            _image!,
                            timestamp,
                            duration,
                            flags,
                            RealtimeDeadline);
                    }
                    finally
                    {
                        End();
                    }

                    ThrowIfAbandoned();
                    EnsureSuccess(result, "encode a VP8 frame");
                    _owner.LastSubmittedTimestamp = timestamp;
                    _owner.LastSubmittedDuration = duration;

                    _forceKeyFrame = false;
                    IntPtr iterator = IntPtr.Zero;
                    byte[]? encoded = null;
                    var packet = vpx_encoder.VpxCodecGetCxData(_context!, (void**)&iterator);
                    while (packet is not null)
                    {
                        if (packet.Kind == VpxCodecCxPktKind.VPX_CODEC_CX_FRAME_PKT)
                        {
                            encoded = new byte[checked((int)packet.data.Raw.Sz)];
                            Marshal.Copy(packet.data.Raw.Buf, encoded, 0, encoded.Length);
                        }

                        packet = vpx_encoder.VpxCodecGetCxData(_context!, (void**)&iterator);
                    }

                    return encoded ?? [];
                }
            }
        }

        /// <summary>Applies both rate targets with one native call; a failure recreates the context.</summary>
        private void ApplyRates(int kbps, int fps)
        {
            var previousKbps = _appliedKbps;
            var previousFps = _appliedFps;
            _configuration!.RcTargetBitrate = (uint)kbps;
            _configuration.KfMaxDist = KeyFrameInterval(fps);
            var started = Stopwatch.GetTimestamp();
            bool applied;
            Begin("vpx_codec_enc_config_set");
            try
            {
                applied = _owner._setConfiguration(_context!, _configuration) == VpxCodecErrT.VPX_CODEC_OK;
            }
            catch (EntryPointNotFoundException)
            {
                // Older native runtimes retain the existing recreate-and-key-frame behavior.
                applied = false;
            }
            catch (Exception ex)
            {
                // Never reuse a context after an unexpected partial configuration failure.
                LogReconfigurationFailed(_owner._log, ex);
                applied = false;
            }
            finally
            {
                End();
            }

            ThrowIfAbandoned();
            if (!applied)
            {
                Reset();
                return;
            }

            if (fps != previousFps) _durationRemainder = 0;
            _appliedKbps = kbps;
            _appliedFps = fps;
            _forceKeyFrame = true;
            Interlocked.Increment(ref _owner._reconfigurationCount);
            LogReconfigured(_owner._log, previousKbps, kbps, previousFps, fps,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        private void Initialise(uint width, uint height, int kbps, int fps)
        {
            var nativeVersion = _owner._getNativeVersion();
            var encoderAbiVersion = EncoderAbiVersionFor(nativeVersion);
            if (encoderAbiVersion == 0)
                throw new PlatformNotSupportedException(
                    $"VP8 encoding requires a reviewed libvpx configuration layout (1.9.0 ABI 23 or 1.17.0 ABI 39); native version {nativeVersion} is unsupported.");

            VpxCodecCtx? candidateContext = new();
            VpxImage? candidateImage = null;
            VpxCodecEncCfg? candidateConfiguration = null;
            var contextInitialised = false;

            try
            {
                var configuration = candidateConfiguration = new NativeEncoderConfiguration();
                var codecInterface = vp8cx.VpxCodecVp8Cx();

                EnsureSuccess(
                    vpx_encoder.VpxCodecEncConfigDefault(codecInterface, configuration, 0),
                    "load the VP8 encoder defaults");

                configuration.GW = width;
                configuration.GH = height;
                configuration.GThreads = (uint)Threads;
                configuration.GErrorResilient = DefaultErrorResilientFlag;
                configuration.GLagInFrames = 0;
                configuration.RcEndUsage = VpxRcMode.VPX_CBR;
                configuration.RcTargetBitrate = (uint)kbps;
                // Native dropping happens before VP8 reference state advances, unlike dropping an
                // already encoded access unit in the transport loop.
                configuration.RcDropframeThresh = 30;
                configuration.RcResizeAllowed = 0;
                configuration.RcMinQuantizer = 2;
                // Prevent the worst VP8 quantizer from turning small desktop text into blocks. The
                // bounded frame queue still drops obsolete work if the endpoint cannot keep up.
                configuration.RcMaxQuantizer = 56;
                configuration.KfMode = VpxKfMode.VPX_KF_AUTO;
                configuration.KfMinDist = 0;
                configuration.KfMaxDist = KeyFrameInterval(fps);

                // libvpx caches this ratio at initialization. Keep it fixed when FPS changes.
                using (var timebase = new VpxRational { Num = 1, Den = ClockRate })
                {
                    configuration.GTimebase = timebase;
                }

                Begin("vpx_codec_enc_init");
                try
                {
                    EnsureSuccess(
                        vpx_encoder.VpxCodecEncInitVer(
                            candidateContext,
                            codecInterface,
                            configuration,
                            0,
                            encoderAbiVersion),
                        "initialise the VP8 encoder");
                }
                finally
                {
                    End();
                }
                contextInitialised = true;
                ThrowIfAbandoned();

                EnsureSuccess(
                    VpxCodecControlInt(
                        candidateContext.__Instance,
                        (int)Vp8eEncControlId.VP8E_SET_CPUUSED,
                        RealtimeCpuUsed),
                    "select the realtime VP8 CPU profile");
                EnsureSuccess(
                    VpxCodecControlInt(
                        candidateContext.__Instance,
                        (int)Vp8eEncControlId.VP8E_SET_NOISE_SENSITIVITY,
                        0),
                    "disable camera-noise search for desktop content");
                EnsureSuccess(
                    VpxCodecControlInt(
                        candidateContext.__Instance,
                        (int)Vp8eEncControlId.VP8E_SET_SCREEN_CONTENT_MODE,
                        DesktopScreenContentMode),
                    "enable the VP8 desktop screen-content profile");
                EnsureSuccess(
                    VpxCodecControlInt(
                        candidateContext.__Instance,
                        (int)Vp8eEncControlId.VP8E_SET_MAX_INTRA_BITRATE_PCT,
                        MaximumIntraBitratePercent),
                    "bound the VP8 key-frame size");

                candidateImage = new VpxImage();
                _context = candidateContext;
                _image = candidateImage;
                _configuration = candidateConfiguration;
                candidateContext = null;
                candidateImage = null;
                candidateConfiguration = null;

                _width = width;
                _height = height;
                _presentationTimestamp = 0;
                _durationRemainder = 0;
                _forceKeyFrame = true;
                _appliedKbps = kbps;
                _appliedFps = fps;
                Interlocked.Increment(ref _owner._initialisationCount);
                LogEncoderStarted(_owner._log, width, height, Threads, kbps, fps);
            }
            finally
            {
                try
                {
                    if (candidateContext is not null)
                    {
                        try
                        {
                            if (contextInitialised)
                                _ = vpx_codec.VpxCodecDestroy(candidateContext);
                        }
                        finally
                        {
                            candidateContext.Dispose();
                        }
                    }
                }
                finally
                {
                    try { candidateImage?.Dispose(); }
                    finally { candidateConfiguration?.Dispose(); }
                }
            }
        }

        private void Reset()
        {
            var context = _context;
            var image = _image;
            var configuration = _configuration;
            _context = null;
            _image = null;
            _configuration = null;

            try
            {
                if (context is not null)
                {
                    Begin("vpx_codec_destroy");
                    try { _ = vpx_codec.VpxCodecDestroy(context); }
                    finally { End(); }
                }
            }
            finally
            {
                try { context?.Dispose(); }
                finally
                {
                    try
                    {
                        // VpxImgWrap borrows only the pinned frame; the wrapper owns its descriptor.
                        image?.Dispose();
                    }
                    finally
                    {
                        try { configuration?.Dispose(); }
                        finally
                        {
                            _width = 0;
                            _height = 0;
                            _presentationTimestamp = 0;
                            _durationRemainder = 0;
                            _appliedKbps = 0;
                            _appliedFps = 0;
                            if (!Abandoned)
                            {
                                _owner.LastSubmittedTimestamp = 0;
                                _owner.LastSubmittedDuration = 0;
                            }
                            _forceKeyFrame = true;
                        }
                    }
                }
            }
        }
    }

    [LoggerMessage(1440, LogLevel.Information,
        "VP8 encoder started: {Width}x{Height}, {Threads} libvpx thread(s), {Kbps} kbps, {Fps} fps")]
    private static partial void LogEncoderStarted(ILogger logger, uint width, uint height, int threads, int kbps, int fps);

    [LoggerMessage(1441, LogLevel.Information,
        "VP8 encoder reconfigured: {PreviousKbps} -> {Kbps} kbps, {PreviousFps} -> {Fps} fps in {Milliseconds:0.0} ms")]
    private static partial void LogReconfigured(
        ILogger logger, int previousKbps, int kbps, int previousFps, int fps, double milliseconds);

    [LoggerMessage(1442, LogLevel.Warning,
        "VP8 encoder call {Operation} did not return within {Seconds:0.0} s ({Width}x{Height}, {Threads} libvpx thread(s), {Kbps} kbps, {Fps} fps); abandoned that encoder and starting a single-threaded one with a key frame ({Abandoned} abandoned)")]
    private static partial void LogEncoderStalled(ILogger logger, string operation, double seconds,
        int width, int height, int threads, int kbps, int fps, int abandoned);

    [LoggerMessage(1443, LogLevel.Information, "An abandoned VP8 encoder call ({Operation}) returned; its thread released the context")]
    private static partial void LogAbandonedEncoderReturned(ILogger logger, string operation);

    [LoggerMessage(1444, LogLevel.Error, "The VP8 encoder stalled {Count} times; software video stopped for this session")]
    private static partial void LogEncoderStopped(ILogger logger, int count);

    [LoggerMessage(1445, LogLevel.Warning, "VP8 reconfiguration failed; the encoder restarts with the new targets")]
    private static partial void LogReconfigurationFailed(ILogger logger, Exception exception);

    [LoggerMessage(1446, LogLevel.Warning, "Releasing a VP8 encoder context failed")]
    private static partial void LogEncoderReleaseFailed(ILogger logger, Exception exception);
}

/// <summary>Encode time percentiles and the current state of the software VP8 encoder.</summary>
internal readonly record struct Vp8EncoderDiagnostics(
    double EncodeP50Ms,
    double EncodeP95Ms,
    string State,
    int Threads,
    int AbandonedEncoders,
    long FramesEncoded);

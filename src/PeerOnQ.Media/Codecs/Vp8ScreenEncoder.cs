using System.Runtime.InteropServices;
using vpxmd;

namespace PeerOnQ.Media.Codecs;

/// <summary>
/// VP8 encoder configured for low-latency desktop content. The upstream convenience encoder in
/// SIPSorceryMedia.Encoders 10.0.4 applies its requested bitrate before loading libvpx defaults,
/// which resets the value. This narrow wrapper configures rate control after defaults and owns
/// dimension/bitrate changes so every restart begins with a key frame.
/// </summary>
internal sealed class Vp8ScreenEncoder : IDisposable
{
    [DllImport("vpxmd", CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpx_codec_control_")]
    private static extern VpxCodecErrT VpxCodecControlInt(nint codec, int controlId, int value);

    private const int EncoderAbiVersion = 23;
    private const int RealtimeDeadline = 1;
    private const int ForceKeyFrameFlag = 1;
    private const uint DefaultErrorResilientFlag = 1;
    // Chromium Remote Desktop uses libvpx's fastest VP8 realtime setting for full-screen desktop
    // frames. The libvpx default (0) spends far too much CPU on motion search and starves capture.
    private const int RealtimeCpuUsed = 16;
    private const int DesktopScreenContentMode = 1;
    private const int MaximumVp8Dimension = 16_383;
    // One 8K I420 frame is about 47.5 MiB; bound the pinned frame and native working set.
    private const int MaximumI420FrameBytes = 64 * 1024 * 1024;
    private readonly Lock _gate = new();

    private VpxCodecCtx? _context;
    private VpxImage? _image;
    private uint _width;
    private uint _height;
    private long _presentationTimestamp;
    private bool _forceKeyFrame = true;
    private bool _disposed;
    private int _targetKbps;
    private int _targetFps;

    public Vp8ScreenEncoder(int targetKbps, int targetFps)
    {
        _targetKbps = ClampBitrate(targetKbps);
        _targetFps = ClampFrameRate(targetFps);
    }

    internal int TargetKbps
    {
        get
        {
            lock (_gate) return _targetKbps;
        }
    }

    internal void SetTargetKbps(int targetKbps)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var bounded = ClampBitrate(targetKbps);
            if (bounded == _targetKbps) return;

            _targetKbps = bounded;
            ResetEncoder();
        }
    }

    internal void SetTargetFps(int targetFps)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var bounded = ClampFrameRate(targetFps);
            if (bounded == _targetFps) return;

            _targetFps = bounded;
            ResetEncoder();
        }
    }

    internal void ForceKeyFrame()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _forceKeyFrame = true;
        }
    }

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

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_context is null || _width != (uint)width || _height != (uint)height)
            {
                ResetEncoder();
                Initialise((uint)width, (uint)height);
            }

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

                    var flags = _forceKeyFrame ? ForceKeyFrameFlag : 0;
                    var result = vpx_encoder.VpxCodecEncode(
                        _context!,
                        _image!,
                        _presentationTimestamp++,
                        1,
                        flags,
                        RealtimeDeadline);
                    EnsureSuccess(result, "encode a VP8 frame");

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
    }

    private void Initialise(uint width, uint height)
    {
        VpxCodecCtx? candidateContext = new();
        VpxImage? candidateImage = null;
        var contextInitialised = false;

        try
        {
            using var configuration = new VpxCodecEncCfg();
            var codecInterface = vp8cx.VpxCodecVp8Cx();

            EnsureSuccess(
                vpx_encoder.VpxCodecEncConfigDefault(codecInterface, configuration, 0),
                "load the VP8 encoder defaults");

            configuration.GW = width;
            configuration.GH = height;
            configuration.GThreads = (uint)Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            configuration.GErrorResilient = DefaultErrorResilientFlag;
            configuration.GLagInFrames = 0;
            configuration.RcEndUsage = VpxRcMode.VPX_CBR;
            configuration.RcTargetBitrate = (uint)_targetKbps;
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
            configuration.KfMaxDist = (uint)(_targetFps * 2);

            using (var timebase = new VpxRational { Num = 1, Den = _targetFps })
            {
                configuration.GTimebase = timebase;
            }

            EnsureSuccess(
                vpx_encoder.VpxCodecEncInitVer(
                    candidateContext,
                    codecInterface,
                    configuration,
                    0,
                    EncoderAbiVersion),
                "initialise the VP8 encoder");
            contextInitialised = true;

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

            candidateImage = new VpxImage();
            _context = candidateContext;
            _image = candidateImage;
            candidateContext = null;
            candidateImage = null;

            _width = width;
            _height = height;
            _presentationTimestamp = 0;
            _forceKeyFrame = true;
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
                candidateImage?.Dispose();
            }
        }
    }

    private void ResetEncoder()
    {
        var context = _context;
        var image = _image;
        _context = null;
        _image = null;

        try
        {
            if (context is not null)
                _ = vpx_codec.VpxCodecDestroy(context);
        }
        finally
        {
            context?.Dispose();
            // VpxImgWrap uses the caller-owned pinned frame and allocates no native image buffer.
            // The generated wrapper owns only the descriptor allocated by its constructor.
            image?.Dispose();

            _width = 0;
            _height = 0;
            _presentationTimestamp = 0;
            _forceKeyFrame = true;
        }
    }

    private static void EnsureSuccess(VpxCodecErrT result, string operation)
    {
        if (result != VpxCodecErrT.VPX_CODEC_OK)
            throw new InvalidOperationException($"Could not {operation}: {vpx_codec.VpxCodecErrToString(result)}.");
    }

    private static int ClampBitrate(int targetKbps) => Math.Clamp(targetKbps, 100, 50_000);

    private static int ClampFrameRate(int targetFps) => Math.Clamp(targetFps, 1, 120);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ResetEncoder();
            _disposed = true;
        }
    }
}

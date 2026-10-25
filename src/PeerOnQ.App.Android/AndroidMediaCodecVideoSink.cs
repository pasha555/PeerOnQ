using System.Diagnostics;
using System.Security.Cryptography;
using Android.Graphics;
using Android.Media;
using Android.Views;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media;
using PeerOnQ.Platform.Android;

namespace PeerOnQ.App.Android;

/// <summary>Queues authenticated VP8 into Android's platform decoder and renders to a Surface.</summary>
public sealed class AndroidMediaCodecVideoSink : Java.Lang.Object, IEncodedVideoFrameSink, ISurfaceHolderCallback
{
    private const string Vp8MimeType = "video/x-vnd.on2.vp8";
    private readonly ISurfaceHolder _holder;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PendingFrame> _pendingFrames = [];
    private MediaCodec? _codec;
    private int _width;
    private int _height;
    private long _nextPresentationTimeUs;
    private bool _surfaceReady;
    private bool _disposed;

    public AndroidMediaCodecVideoSink(ISurfaceHolder holder)
    {
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _surfaceReady = holder.Surface?.IsValid == true;
        holder.AddCallback(this);
    }

    public event EventHandler<EncodedVideoFramePresentation>? FramePresented;

    public int VideoWidth
    {
        get { lock (_gate) return _width; }
    }

    public int VideoHeight
    {
        get { lock (_gate) return _height; }
    }

    public bool TrySubmit(AuthenticatedVp8Frame frame)
    {
        if (frame.EncodedBytes.IsEmpty) return false;
        lock (_gate)
        {
            if (_disposed || !_surfaceReady || _holder.Surface?.IsValid != true) return false;

            if (Vp8KeyFrameParser.TryReadDimensions(frame.EncodedBytes.Span, out var dimensions)
                && (_codec is null || dimensions.Width != _width || dimensions.Height != _height))
            {
                ConfigureDecoder(dimensions);
            }
            if (_codec is null) return false;

            try
            {
                DrainOutput();
                var inputIndex = _codec.DequeueInputBuffer(0);
                if (inputIndex < 0) return false;
                using var inputBuffer = _codec.GetInputBuffer(inputIndex);
                if (inputBuffer is null || inputBuffer.Capacity() < frame.EncodedBytes.Length) return false;

                var payload = frame.EncodedBytes.ToArray();
                try
                {
                    inputBuffer.Clear();
                    inputBuffer.Put(payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }

                var presentationTimeUs = ++_nextPresentationTimeUs;
                _pendingFrames[presentationTimeUs] = new PendingFrame(
                    frame.EncodedBytes.Length,
                    frame.CaptureTimestampUnixMicroseconds,
                    frame.CaptureSequenceNumber,
                    frame.PeerClockEstimate);
                _codec.QueueInputBuffer(
                    inputIndex,
                    0,
                    frame.EncodedBytes.Length,
                    presentationTimeUs,
                    MediaCodecBufferFlags.None);
                DrainOutput();
                return true;
            }
            catch (Java.Lang.Exception)
            {
                ResetDecoder();
                return false;
            }
        }
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        lock (_gate) _surfaceReady = holder.Surface?.IsValid == true;
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        lock (_gate) _surfaceReady = holder.Surface?.IsValid == true;
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        lock (_gate)
        {
            _surfaceReady = false;
            ResetDecoder();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _holder.RemoveCallback(this);
                    ResetDecoder();
                }
            }
        }
        base.Dispose(disposing);
    }

    private void ConfigureDecoder(Vp8FrameDimensions dimensions)
    {
        ResetDecoder();
        var surface = _holder.Surface;
        if (!_surfaceReady || surface?.IsValid != true) return;

        var codec = MediaCodec.CreateDecoderByType(Vp8MimeType)
                    ?? throw new InvalidOperationException("This Android device has no VP8 decoder.");
        try
        {
            using var format = MediaFormat.CreateVideoFormat(Vp8MimeType, dimensions.Width, dimensions.Height)
                               ?? throw new InvalidOperationException("Android could not create the VP8 media format.");
            codec.Configure(format, surface, (MediaCrypto?)null, MediaCodecConfigFlags.None);
            codec.Start();
            _codec = codec;
            _width = dimensions.Width;
            _height = dimensions.Height;
        }
        catch
        {
            codec.Dispose();
            throw;
        }
    }

    private void DrainOutput()
    {
        if (_codec is null) return;
        using var info = new MediaCodec.BufferInfo();
        while (true)
        {
            var outputIndex = (int)_codec.DequeueOutputBuffer(info, 0);
            if (outputIndex < 0) return;

            _codec.ReleaseOutputBuffer(outputIndex, render: true);
            if (!_pendingFrames.Remove(info.PresentationTimeUs, out var pending)) continue;
            FramePresented?.Invoke(this, new EncodedVideoFramePresentation(
                new PresentedVideoFrame(
                    Stopwatch.GetTimestamp(),
                    _width,
                    _height,
                    pending.CaptureTimestampUnixMicroseconds,
                    pending.CaptureSequenceNumber,
                    pending.PeerClockEstimate),
                pending.EncodedBytes));
        }
    }

    private void ResetDecoder()
    {
        if (_codec is not null)
        {
            try { _codec.Stop(); }
            catch (Java.Lang.Exception) { }
            _codec.Dispose();
            _codec = null;
        }
        _pendingFrames.Clear();
        _width = 0;
        _height = 0;
    }

    private sealed record PendingFrame(
        int EncodedBytes,
        long CaptureTimestampUnixMicroseconds,
        long CaptureSequenceNumber,
        PeerClockEstimate? PeerClockEstimate);
}

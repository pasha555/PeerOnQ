using System.Diagnostics;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;

namespace PeerOnQ.Media;

public sealed partial class WebRtcMediaSession
{
    private const string VideoCodecSdpAttribute = "a=x-peeronq-video-payload:1;vp8,h264";
    private HardwareVideoEncodingController? _hardwareEncoding;
    private IH264VideoDecoder? _h264Decoder;
    private bool _canOfferH264;
    private bool _videoCodecOffered;
    private int _videoCodecNegotiated;
    private VideoPayloadCodec? _receivedVideoCodec;
    private LatestDecodedVideoWorker? _videoPresentation;
    private readonly RemotePixelFormat _viewerOutputFormat;
    private readonly FrameBufferPool _presentationBuffers = new();

    public bool IsH264PayloadNegotiated => Volatile.Read(ref _videoCodecNegotiated) != 0;

    private void InitializeVideoCodecs(Func<IHardwareH264Encoder?>? hardwareFactory, IH264VideoDecoder? decoder)
    {
        _h264Decoder = decoder;
        _hardwareEncoding = new HardwareVideoEncodingController(hardwareFactory);
        _canOfferH264 = _permissions.HasFlag(SessionPermission.ViewScreen)
            && (Role == SessionRole.Sharer ? hardwareFactory is not null : decoder is not null && _encodedVideoSink is null);
        if (Role == SessionRole.Viewer && _permissions.HasFlag(SessionPermission.ViewScreen) && _encodedVideoSink is null)
            _videoPresentation = new LatestDecodedVideoWorker(
                _viewerOutputFormat == RemotePixelFormat.Bgra32
                    ? ConvertDecodedToBgra
                    : frame => frame.Nv12 is { } nv12
                        ? ((IDeferredH264VideoDecoder)_h264Decoder!).ConvertNv12ToBgr(nv12)
                        : I420BgrConverter.Convert(frame.Planes, frame.Width, frame.Height),
                PresentDecodedVideo, error => LogUndecodableFrame(_log, error));
    }

    /// <summary>
    /// Writes the decoded planes straight into a reusable BGRA32 presentation buffer, so the
    /// renderer only copies rows and the conversion stays on the bounded presentation worker.
    /// </summary>
    private byte[] ConvertDecodedToBgra(DecodedVideoPresentation frame)
    {
        var target = _presentationBuffers.Rent(YuvBgraConverter.OutputLength(frame.Width, frame.Height));
        try
        {
            if (frame.Nv12 is { } nv12) YuvBgraConverter.ConvertNv12(nv12, target);
            else YuvBgraConverter.ConvertI420(frame.Planes, frame.Width, frame.Height, target);
            return target;
        }
        catch
        {
            _presentationBuffers.Return(target);
            throw;
        }
    }

    internal static bool HasH264Payload(string sdp)
    {
        var lines = sdp.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Count(line => line.StartsWith("a=x-peeronq-video-payload:", StringComparison.Ordinal)) == 1
            && lines.Contains(VideoCodecSdpAttribute, StringComparer.Ordinal);
    }

    private string AdvertiseVideoCodec(string sdp)
    {
        _videoCodecOffered = _canOfferH264;
        return _canOfferH264 ? AppendVideoCodec(sdp) : sdp;
    }

    private static string AppendVideoCodec(string sdp) =>
        string.Concat(sdp.TrimEnd('\r', '\n'), "\r\n", VideoCodecSdpAttribute, "\r\n");

    private void SetVideoCodecNegotiated(bool negotiated)
    {
        if (Interlocked.Exchange(ref _videoCodecNegotiated, negotiated ? 1 : 0) == (negotiated ? 1 : 0)) return;
        _lossRecovery.RequestKeyFrame();
        lock (_decoderGate) _receivedVideoCodec = null;
        _videoPresentation?.Clear();
    }

    private (VideoPayloadCodec Codec, byte[] Bytes) EncodeVideoFrame(
        int width, int height, byte[] frame, CapturedPixelFormat format = CapturedPixelFormat.I420)
    {
        var encoded = _hardwareEncoding!.Encode(_encoder!, IsH264PayloadNegotiated,
            width, height, frame, Volatile.Read(ref _targetBitrateKbps), _rateLimiter.TargetFps,
            _lossRecovery.ConsumeKeyFrameRequest(), format);
        var actual = _hardwareEncoding.Selection;
        if (actual != Encoder)
        {
            Encoder = actual;
            LogEncoderSelected(_log, SessionId, Encoder);
        }
        return encoded;
    }

    internal bool TryAcceptVideoCodec(VideoPayloadCodec codec, bool keyFrame, out bool changed)
    {
        lock (_decoderGate)
        {
            changed = _receivedVideoCodec != codec;
            if (changed && !keyFrame) return false;
            _receivedVideoCodec = codec;
            if (changed) _videoPresentation?.Clear();
            return true;
        }
    }

    private void PresentDecodedVideo(DecodedVideoPresentation frame, byte[] pixels) =>
        RemoteFrameReceived?.Invoke(this, new RemoteVideoFrame(
            frame.Width, frame.Height, pixels, _viewerOutputFormat, TimeSpan.Zero)
        {
            BufferOwnershipCanTransfer = true,
            ReturnBuffer = _viewerOutputFormat == RemotePixelFormat.Bgra32 ? _presentationBuffers.Return : null,
            PipelineDecodedTimestamp = frame.DecodedTimestamp,
            CaptureTimestampUnixMicroseconds = frame.CaptureTimestampUnixMicroseconds,
            CaptureSequenceNumber = frame.SequenceNumber,
            PeerClockEstimate = frame.PeerClockEstimate,
        });

    private void DecodeH264Frame(int width, int height, byte[] frame, bool reset,
        long captureTimestamp, long sequenceNumber)
    {
        if (_h264Decoder is IDeferredH264VideoDecoder deferred && _videoPresentation is { } presentation)
        {
            DecodedNv12Frame[] decoded;
            lock (_decoderGate) decoded = deferred.DecodeNv12(width, height, frame, reset);
            if (decoded.Any(image => image is null || image.Width != width || image.Height != height || !image.HasValidLayout))
            {
                foreach (var image in decoded)
                    if (image?.Pixels is { } pixels) CryptographicOperations.ZeroMemory(pixels);
                throw new InvalidOperationException("The H.264 decoder returned an invalid NV12 frame.");
            }
            foreach (var image in decoded)
            {
                _statistics.FrameDecoded(frame.Length, width, height);
                presentation.Enqueue(new DecodedVideoPresentation(width, height, image.Pixels,
                    Stopwatch.GetTimestamp(), captureTimestamp, sequenceNumber, GetPeerClockEstimate(), image));
            }
            return;
        }
        RemoteVideoFrame[] images;
        lock (_decoderGate)
        {
            images = (_h264Decoder ?? throw new InvalidOperationException("H.264 was not negotiated with a decoder."))
                .Decode(width, height, frame, reset);
        }
        foreach (var image in images)
        {
            if (image.Width != width || image.Height != height || image.Format != RemotePixelFormat.Bgr24
                || image.Pixels.Length != (long)width * height * 3)
                throw new InvalidOperationException("The H.264 decoder returned an invalid video frame.");
            _statistics.FrameDecoded(frame.Length, width, height);
            RemoteFrameReceived?.Invoke(this, image with
            {
                PipelineDecodedTimestamp = Stopwatch.GetTimestamp(),
                CaptureTimestampUnixMicroseconds = captureTimestamp,
                CaptureSequenceNumber = sequenceNumber,
                PeerClockEstimate = GetPeerClockEstimate(),
            });
        }
    }
}

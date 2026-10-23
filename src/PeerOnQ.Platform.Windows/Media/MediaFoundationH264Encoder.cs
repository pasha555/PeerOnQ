using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PeerOnQ.Media.Codecs;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;
using static PeerOnQ.Platform.Windows.Media.MediaFoundationVideoUtilities;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>Hardware MFT only, one outstanding frame, asynchronous events with bounded waits.</summary>
public sealed class MediaFoundationH264Encoder : IHardwareH264Encoder
{
    /// <summary>Safety-net IDR interval; normal recovery uses explicit keyframe requests.</summary>
    internal const int KeyFrameIntervalSeconds = 5;

    private readonly bool _retainFillerForDiagnostics;
    private readonly Lock _gate = new();
    private IMFActivate? _activation;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private MediaFoundationEncoderEvents? _eventPump;
    private bool _started;
    private bool _disposed;
    private int _width, _height, _bitrate, _frameRate, _inputCredits;
    private long _timestamp;
    private byte[] _sequenceHeader = [];
    private byte[]? _nv12Scratch;
    public string Name { get; private set; } = "Media Foundation hardware H.264";
    internal double LastFormatConversionMilliseconds { get; private set; }
    internal double LastNativeEncodeMilliseconds { get; private set; }
    internal int InitialisationCount { get; private set; }
    internal int DynamicFrameRateChanges { get; private set; }
    internal int DynamicFrameRateFallbacks { get; private set; }
    internal bool DynamicFormatChangeSupported { get; private set; }
    internal long? LastOutputSampleTime { get; private set; }
    internal long? LastOutputSampleDuration { get; private set; }
    internal int LastNativeEncodedBytes { get; private set; }
    internal int LastFillerBytesRemoved { get; private set; }
    internal int LastEncodedOutputBytes { get; private set; }
    internal int Nv12ScratchAllocations { get; private set; }
    internal int Nv12ScratchBytes { get { lock (_gate) return _nv12Scratch?.Length ?? 0; } }
    internal bool IsNv12ScratchCleared
    {
        get { lock (_gate) return _nv12Scratch is null || _nv12Scratch.AsSpan().IndexOfAnyExcept((byte)0) < 0; }
    }

    public MediaFoundationH264Encoder() : this(retainFillerForDiagnostics: false) { }

    // Only friend tests can obtain the native baseline for independent exact-pixel comparison.
    // Public construction and IHardwareH264Encoder always remove verified non-reference filler.
    internal MediaFoundationH264Encoder(bool retainFillerForDiagnostics) =>
        _retainFillerForDiagnostics = retainFillerForDiagnostics;

    public bool SupportsDimensions(int width, int height) => CompatibleH264Dimensions(width, height);

    public byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame) =>
        Encode(width, height, frame, bitrateKbps, frameRate, forceKeyFrame, nv12Input: false);

    /// <summary>
    /// Encodes a GPU-converted NV12 frame directly: no managed scratch, interleave or extra copy;
    /// the only CPU work left is the synchronous copy into the native input sample.
    /// </summary>
    public byte[] EncodeNv12(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame) =>
        Encode(width, height, frame, bitrateKbps, frameRate, forceKeyFrame, nv12Input: true);

    private byte[] Encode(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame,
        bool nv12Input)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CompatibleH264Dimensions(width, height))
                throw new ArgumentOutOfRangeException(nameof(width), "This source size requires the VP8 fallback.");
            // Validate before entering a driver or allocating its native buffers.
            if (nv12Input) Nv12Planes.Validate(width, height, frame);
            else ValidateI420(width, height, frame);
            var conversionBegan = Stopwatch.GetTimestamp();
            var nv12 = nv12Input ? frame : GetNv12Scratch(frame.Length);
            bitrateKbps = Math.Clamp(bitrateKbps, 100, 50_000);
            frameRate = Math.Clamp(frameRate, 1, 60);
            var duration = 10_000_000L / frameRate;
            long nativeBegan;
            IMFSample sample;
            try
            {
                if (!nv12Input) I420ToNv12(width, height, frame, nv12);
                LastFormatConversionMilliseconds = Stopwatch.GetElapsedTime(conversionBegan).TotalMilliseconds;
                nativeBegan = Stopwatch.GetTimestamp();
                if (_transform is null || width != _width || height != _height)
                    ReinitialisePreservingTimestamp(width, height, bitrateKbps, frameRate);
                else if (frameRate != _frameRate && !TryChangeFrameRate(width, height, bitrateKbps, frameRate))
                {
                    DynamicFrameRateFallbacks++;
                    ReinitialisePreservingTimestamp(width, height, bitrateKbps, frameRate);
                }
                if (_bitrate != bitrateKbps)
                {
                    if (!SetCodecValue(_transform!, MeanBitRate, (uint)bitrateKbps * 1000))
                        ReinitialisePreservingTimestamp(width, height, bitrateKbps, frameRate);
                    _bitrate = bitrateKbps;
                }
                if (forceKeyFrame && !SetCodecValue(_transform!, ForceKeyFrame, 1))
                    ReinitialisePreservingTimestamp(width, height, bitrateKbps, frameRate);
                WaitForEvent(MediaEventTypes.TransformNeedInput, TimeSpan.FromSeconds(2));
                _inputCredits--;
                sample = Sample(nv12, _timestamp, duration, _transform!.GetInputStreamInfo(0).Alignment);
            }
            finally
            {
                // Sample() completed its synchronous native copy, or failed without handing
                // these bytes to a driver. No plaintext remains in the reusable managed buffer.
                // A caller-owned NV12 frame is not this encoder's scratch and is left intact.
                if (!nv12Input) CryptographicOperations.ZeroMemory(nv12);
            }
            using var inputSample = sample;
            _timestamp += duration;
            _transform!.ProcessInput(0, inputSample, 0);
            WaitForEvent(MediaEventTypes.TransformHaveOutput, TimeSpan.FromMilliseconds(750));
            var encoded = ReadOutput(_transform, VideoCodecLimits.MaximumPayloadBytes, out var streamChange,
                out var sampleTime, out var sampleDuration);
            LastOutputSampleTime = sampleTime;
            LastOutputSampleDuration = sampleDuration;
            if (streamChange || encoded is null)
                throw new InvalidOperationException("The hardware encoder did not produce its promised output.");
            LastNativeEncodeMilliseconds = Stopwatch.GetElapsedTime(nativeBegan).TotalMilliseconds;
            LastNativeEncodedBytes = encoded.Length;
            var filtered = _retainFillerForDiagnostics ? encoded : H264FillerDataFilter.RemoveFromNativeSample(encoded);
            LastFillerBytesRemoved = encoded.Length - filtered.Length;
            if (!ReferenceEquals(filtered, encoded)) CryptographicOperations.ZeroMemory(encoded);
            encoded = filtered;
            // MF_MT_MPEG_SEQUENCE_HEADER contains Annex B SPS/PPS on the H.264 MFT. Include it
            // on IDR units so a new peer, resized decoder, or post-loss recovery can start alone.
            if (_sequenceHeader.Length != 0 && HasNal(encoded, 5)
                && (!HasNal(encoded, 7) || !HasNal(encoded, 8)))
            {
                if (encoded.Length > VideoCodecLimits.MaximumPayloadBytes - _sequenceHeader.Length)
                    throw new InvalidOperationException("The encoded video access unit is too large.");
                var complete = new byte[_sequenceHeader.Length + encoded.Length];
                _sequenceHeader.CopyTo(complete, 0);
                encoded.CopyTo(complete, _sequenceHeader.Length);
                LastEncodedOutputBytes = complete.Length;
                return complete;
            }
            LastEncodedOutputBytes = encoded.Length;
            return encoded;
        }
    }

    private byte[] GetNv12Scratch(int length)
    {
        if (_nv12Scratch is null || _nv12Scratch.Length != length)
        {
            if (_nv12Scratch is not null) CryptographicOperations.ZeroMemory(_nv12Scratch);
            _nv12Scratch = GC.AllocateUninitializedArray<byte>(length);
            Nv12ScratchAllocations++;
        }
        return _nv12Scratch;
    }

    internal bool NativeTypesMatchFormat(int width, int height, int frameRate)
    {
        lock (_gate)
        {
            if (_transform is null) return false;
            using var output = _transform.GetOutputCurrentType(0);
            using var input = _transform.GetInputCurrentType(0);
            return Matches(output, H264, width, height, frameRate) && Matches(input, Nv12, width, height, frameRate);
        }
    }

    private bool TryChangeFrameRate(int width, int height, int bitrate, int frameRate)
    {
        try
        {
            using var attributes = _transform!.Attributes;
            if (MFGetAttributeUInt32(attributes, TransformAttributeKeys.MftSupportDynamicFormatChange, 0) == 0)
                return false;
            using var output = VideoType(H264, width, height, frameRate);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate * 1000);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u);
            using var input = VideoType(Nv12, width, height, frameRate);
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)width);
            // Encoders require a matching output type first. Test input against the accepted
            // new output, and reconstruct if either this or any subsequent step is rejected.
            _transform.SetOutputType(0, output, 1);
            _transform.SetOutputType(0, output, 0);
            _transform.SetInputType(0, input, 1);
            _transform.SetInputType(0, input, 0);
            if (!SetCodecValue(_transform, MeanBitRate, (uint)bitrate * 1000)
                || !SetCodecValue(_transform, ForceKeyFrame, 1))
                return false;
            using var currentOutput = _transform.GetOutputCurrentType(0);
            using var currentInput = _transform.GetInputCurrentType(0);
            if (!Matches(currentOutput, H264, width, height, frameRate)
                || !Matches(currentInput, Nv12, width, height, frameRate))
                return false;
            _sequenceHeader = currentOutput.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var size).Success
                && size > 0 && size <= 64 * 1024
                ? currentOutput.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader) : [];
            _frameRate = frameRate;
            _bitrate = bitrate;
            DynamicFrameRateChanges++;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool Matches(IMFMediaType type, Guid subtype, int width, int height, int frameRate) =>
        type.GetGUID(MediaTypeAttributeKeys.Subtype) == subtype
        && type.GetUInt64(MediaTypeAttributeKeys.FrameSize) == PackSize((uint)width, (uint)height)
        && type.GetUInt64(MediaTypeAttributeKeys.FrameRate) == PackRatio(frameRate, 1);

    private void ReinitialisePreservingTimestamp(int width, int height, int bitrate, int frameRate)
    {
        var nextTimestamp = _timestamp;
        Initialise(width, height, bitrate, frameRate);
        _timestamp = nextTimestamp;
    }

    private static bool HasNal(ReadOnlySpan<byte> bytes, int type)
    {
        for (var index = 0; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] != 0 || bytes[index + 1] != 0) continue;
            var prefix = bytes[index + 2] == 1 ? 3
                : index + 4 < bytes.Length && bytes[index + 2] == 0 && bytes[index + 3] == 1 ? 4 : 0;
            if (prefix != 0 && (bytes[index + prefix] & 31) == type) return true;
        }
        return false;
    }

    private void Initialise(int width, int height, int bitrate, int frameRate)
    {
        Reset();
        MFStartup().CheckError();
        _started = true;
        using var activations = Enumerate(TransformCategoryGuids.VideoEncoder, 0x44,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Nv12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = H264 });
        Exception? lastError = null;
        foreach (var activation in activations)
        {
            IMFTransform? transform = null;
            try
            {
                transform = activation.ActivateObject<IMFTransform>();
                using var attributes = transform.Attributes;
                if (attributes.GetUInt32(TransformAttributeKeys.TransformAsync) == 0)
                    throw new InvalidOperationException("A hardware MFT must use asynchronous processing.");
                DynamicFormatChangeSupported = MFGetAttributeUInt32(attributes, TransformAttributeKeys.MftSupportDynamicFormatChange, 0) != 0;
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                attributes.Set(LowLatency, 1u);
                if (!SetCodecValue(transform, LowLatency, uint.MaxValue, boolean: true))
                    throw new InvalidOperationException("The hardware encoder cannot guarantee low-latency output.");
                _ = SetCodecValue(transform, new("1c0608e9-370c-4710-8a58-cb6181c42423"), 0); // CBR
                _ = SetCodecValue(transform, new("8d390aac-dc5c-4200-b57f-814d04babab2"), 0); // no B frames
                // GOP size. Loss recovery is driven by authenticated PLI/keyframe requests, so a
                // periodic IDR only adds a burst: at 4K a two-second GOP produced a large key
                // frame, a CBR quality dip and a frame-age spike every two seconds. Five seconds bounds
                // corruption when a single loss and its PLI are both lost; RTP gaps also request PLI.
                _ = SetCodecValue(transform, new("95f31b26-95a4-41aa-9303-246a7fc6eef1"),
                    (uint)frameRate * KeyFrameIntervalSeconds);
                using var output = VideoType(H264, width, height, frameRate);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate * 1000);
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u); // Main profile, no frame reordering.
                transform.SetOutputType(0, output, 0);
                using var input = VideoType(Nv12, width, height, frameRate);
                input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)width);
                transform.SetInputType(0, input, 0);
                using var currentOutput = transform.GetOutputCurrentType(0);
                _sequenceHeader = currentOutput.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var size).Success
                    && size > 0 && size <= 64 * 1024
                    ? currentOutput.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader) : [];
                Name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                // Retain an independent COM reference; the enumeration collection owns activation.
                _activation = activation.QueryInterface<IMFActivate>();
                _transform = transform;
                transform = null;
                _events = _transform.QueryInterface<IMFMediaEventGenerator>();
                _eventPump = new MediaFoundationEncoderEvents(_events);
                _eventPump.Start();
                _width = width; _height = height; _bitrate = bitrate; _frameRate = frameRate;
                InitialisationCount++;
                _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
                _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _eventPump?.Stop();
                _events?.Dispose(); _events = null;
                try
                {
                    if (_transform is not null) Close(_activation, _transform);
                    else Close(null, transform);
                }
                finally
                {
                    _activation = null; _transform = null;
                    _eventPump?.Dispose(); _eventPump = null;
                }
                try { activation.ShutdownObject(); } catch (Exception) { }
            }
        }
        Reset();
        throw new InvalidOperationException("No hardware H.264 MFT accepted this screen format.", lastError);
    }

    private void WaitForEvent(MediaEventTypes expected, TimeSpan timeout)
    {
        if (expected == MediaEventTypes.TransformNeedInput && _inputCredits > 0) return;
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            var type = _eventPump!.Next(timeout - Stopwatch.GetElapsedTime(started));
            if (type == MediaEventTypes.TransformNeedInput)
            {
                _inputCredits = Math.Min(2, _inputCredits + 1);
                if (expected == MediaEventTypes.TransformNeedInput) return;
            }
            else if (type == expected) return;
        }
        throw new TimeoutException("The hardware H.264 encoder stopped responding.");
    }

    private void Reset()
    {
        // Native reconfiguration may happen after filling the current managed image.
        // Its separate lifetime ends at the synchronous Sample copy, never here.
        var pump = _eventPump;
        _eventPump = null;
        pump?.Stop();
        try
        {
            _events?.Dispose(); _events = null;
            Close(_activation, _transform);
        }
        finally
        {
            _activation = null; _transform = null;
            pump?.Dispose();
        }
        _inputCredits = 0; _timestamp = 0; _sequenceHeader = [];
        if (_started) { _ = MFShutdown(); _started = false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { Reset(); }
            finally
            {
                if (_nv12Scratch is not null) CryptographicOperations.ZeroMemory(_nv12Scratch);
                _nv12Scratch = null;
            }
        }
    }
}

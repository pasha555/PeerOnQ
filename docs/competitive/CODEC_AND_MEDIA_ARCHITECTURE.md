# Codec and media architecture

PeerOnQ keeps the existing project boundaries because they already separate capture, media,
transport and UI. Creating duplicate `PeerOnQ.Media.Windows` or `Encoding` projects would add names,
not capability.

```text
Windows.Graphics.Capture
  -> D3D11 capture texture
  -> staging texture and GPU-to-CPU map
  -> BGRA-to-I420 conversion/scaling
  -> bounded latest-frame queue (capacity 1)
  -> software libvpx VP8 encoder
  -> RTP/SRTP plus PeerOnQ authenticated video records
  -> software VP8 decoder
  -> BGR24 owned buffer
  -> WinUI WriteableBitmap BGRA32 render
```

## Existing interfaces

- `IScreenCaptureSource` owns explicit target selection and capture lifecycle.
- `IMediaEngine` creates sharer/viewer sessions without UI coupling.
- `IMediaSession` owns WebRTC, data-channel and statistics lifecycle.
- `MediaProfile` supplies immutable initial quality policy.
- `AdaptiveQualityController` supplies bounded network/load adaptation.
- `MediaStatisticsCollector` supplies measured, not nominal, values.

The capture frame carries physical source, user-requested and actual encoder-input dimensions.
The viewer separately records decoder output and successfully rendered dimensions. Codec reporting
states the active software VP8 encoder/decoder and never converts a hardware capability probe into
a false acceleration claim.

`IVideoEncoder`/`IVideoDecoder` are not introduced only to satisfy a document name. The current
codec wrapper is concrete and has one production implementation. An interface becomes justified
when a second reviewed codec path is implemented and resource/fallback behavior is testable.

## Unavoidable current copies

1. WGC D3D11 texture to CPU-readable staging resource.
2. BGRA to I420 conversion/scaling into the encoder input array.
3. VP8 decoder output to a BGR24 owned array.
4. BGR24 to the WinUI `WriteableBitmap` BGRA32 back buffer.

The capture buffer and decoded frame ownership are transferred where possible, avoiding additional
full-frame array duplication. This is a minimal-copy CPU codec path, not a zero-copy GPU path.

## Codec truth

- Active codec: software VP8 via SIPSorcery/SIPSorceryMedia.Encoders/libvpx.
- Hardware probe: Media Foundation reports potential H.264/HEVC devices.
- Active hardware encoder: none.
- Active hardware decoder: none.
- H.264/VP9/AV1: not negotiated by the shipping pipeline.
- Audio: not implemented or negotiated.

Any hardware path must prove device selection, profiles, real encode/decode, device loss, bounded
fallback, teardown, binary redistribution terms and equivalent security protection before it is
advertised.

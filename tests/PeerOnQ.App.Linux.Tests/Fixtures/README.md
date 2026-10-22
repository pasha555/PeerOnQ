These small VP8 fixtures contain only synthetic solid-color I420 frames: a key frame, an inter
frame, a color change, a small resize and a 3840x2160 resize. `Bgr` is the decoded Windows reference result from the pinned
SIPSorceryMedia.Encoders 10.0.4 binding, not ideal BT.601 RGB. Its existing PixelConverter uses a
different coefficient/range convention; Linux must preserve the same established pixel contract.
`SourceI420` and `SourceIdealBgr` retain the input and distinguish those meanings.

The reference Windows `vpxmd.dll` SHA256 is
`9ef05f9448c23de0043c1b9da7d84fd3bc07d3a7aaeb57ce87ca68a52b8622c7`.
Linux tests use the distro-managed `libvpx.so.9`, exercise stateful decode and resize twice with
fresh decoder instances, and retain the strict per-channel tolerance of eight. A separate actual
WebRTC test carries the same fixtures through PNQE protection and the real Linux media receiver.
The synthetic 4K fixture validates decode dimensions and pixels; it does not measure frame rate or
prove presentation on a physical 4K Linux display.

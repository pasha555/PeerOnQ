# File-transfer architecture

Phase 6.5 preserves the existing direct-path transfer design because no measured evidence justifies
a replacement transport.

```text
approved FileTransfer permission
  -> authenticated collaboration v2 channel
  -> 64 KiB streaming chunks
  -> bounded SCTP bulk window
  -> interactive/normal/bulk priority scheduler
  -> partial destination file
  -> chunk and final SHA-256 verification
  -> AMSI scan
  -> atomic final rename
```

The complete source file is never loaded into memory. Resume accepts only a known transfer identity
and exact verified offset. Traversal, reparse escape, insufficient space, unexpected offset,
corrupt chunk/final hash and malware scan failures fail closed. File transfer has no arbitrary
PeerOnQ byte or rate cap; SCTP buffering is a memory/backpressure bound and bulk traffic yields to
interactive waiters.

Current policy deliberately rejects relay-only file sessions with `direct_p2p_required`. This is a
product/security constraint, not an assertion that TURN cannot transport data. 1/10/100 GB
throughput, physical reconnect resume and input-latency-under-transfer evidence remain unmeasured.

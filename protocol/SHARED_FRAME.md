# Shared frame contract (v2)

## Shared-memory layout v2

Names: mapping `Global\RaaiCam.VCam.Frames.v2`. No events: the reader is timer-paced and the writer polls the header.

All little-endian, `#pragma pack(1)`, offsets fixed:

```
ControlHeader (256 bytes, offset 0)
  0   u32 magic            = 0x32464352 ('RCF2')
  4   u32 version          = 2
  8   u32 headerBytes      = 256
  12  u32 slotCount        = 3
  16  u32 slotStride       = 64 + 3110400   (SlotHeader + max NV12 1920x1080)
  20  u32 maxWidth         = 1920
  24  u32 maxHeight        = 1080
  28  u32 _pad0
  -- written by the reader (DLL) --
  32  u64 consumerGeneration   incremented each time the mapping is (re)created; never 0 once valid
  40  u32 consumerActive       1 while ≥1 stream is started
  44  u32 requestedWidth       size the active stream negotiated (0 when inactive)
  48  u32 requestedHeight
  52  u32 _pad1
  56  i64 readerHeartbeatQpc   updated every delivery tick
  -- written by the writer (app) --
  64  u32 writerPid
  68  u32 latestSlot           index of the most recently completed slot (0..2), 0xFFFFFFFF = none
  72  i64 writerHeartbeatQpc   updated every write
  80  .. 255 reserved (zero)

Slot i at offset 256 + i*slotStride:
  SlotHeader (64 bytes)
  0   u64 seq          even = stable, odd = being written (seqlock)
  8   u32 width
  12  u32 height
  16  u32 stride       bytes per Y row, >= width
  20  u32 format       = 0x3231564E ('NV12')
  24  i64 timestampQpc host Stopwatch.GetTimestamp() (QPC) taken by the writer at write time
  32  u64 frameId      monotonically increasing per writer
  40  .. 63 reserved
  then stride*height*3/2 bytes of NV12
```

Writer protocol: pick a slot ≠ `latestSlot` (round-robin among the other two) → `seq++` (odd) → full barrier → write the fields + pixels → full barrier → `seq++` (even) → `latestSlot = i` → `writerHeartbeatQpc = now`.
Writer preconditions: the writer must not write any slot until the header's `magic`, `version`, `headerBytes`, `slotCount`, `slotStride`, `maxWidth` and `maxHeight` are all valid, and it must re-validate them after every change of `consumerGeneration` (the mapping was recreated; its state is not the one the writer last saw).
Mapping creation: only a DLL instance can create the `Global\` mapping. If the reader opens an existing mapping whose `magic` is 0 (a previous creator died before finishing), it initializes the header itself (all fields, `magic` last behind a full barrier).
Reader protocol: read `latestSlot` → `s1 = seq` (must be even) → copy the header fields **once** into locals → validate (format, `width/height == requested`, `stride >= width`, `stride*height*3/2 <= slotStride-64`) → copy the pixels → barrier → `s2 = seq`; accept only if `s1 == s2`. Otherwise retry once with the other complete slots; else reuse the last good frame if it is ≤ 500 ms old, else the card.

Timestamp contract: `timestampQpc` is the host QPC (`Stopwatch.GetTimestamp()`) value taken when the frame is written, not a capture time from another clock. The reader accepts only frames at most 500 ms old and at most 1 s in the future; anything else is never served as live. This is part of the contract: a writer that stamps frames with another clock gets the placeholder card.
Size contract: the writer must write exactly the size the consumer requested (`requestedWidth`/`requestedHeight`); neither the sink nor the reader scales, so the compositor renders at the requested size.

## Versioning

Any layout change bumps `magic`/`version` and the mapping name suffix (e.g. `.v3`), so old writers and readers never interpret a newer layout.

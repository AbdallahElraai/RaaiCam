#include "FrameReader.h"

#include <algorithm>
#include <atomic>
#include <cstring>

#include "SharedFrameV2.h"

namespace raaicam {

namespace {

template <typename T>
inline T ReadOnce(const T& v) {
  return *reinterpret_cast<const volatile T*>(&v);
}

}  // namespace

// Reads one slot under the seqlock into staging_. Returns true only for a consistent, valid read.
// On success staging_ holds `stride*height*3/2` source bytes and stagedStride_ the validated stride.
bool FrameReader::TryReadSlot(const uint8_t* region, uint32_t slot, uint32_t width, uint32_t height,
                              int64_t nowQpc, int64_t qpcFreq, int64_t& tsOut, uint64_t& frameIdOut) {
  const SlotHeader* sh = reinterpret_cast<const SlotHeader*>(region + kHeaderBytes + size_t(slot) * kSlotStride);

  const uint64_t s1 = ReadOnce(sh->seq);
  if (s1 & 1u) return false;  // writer mid-update
  std::atomic_thread_fence(std::memory_order_acquire);

  // Header fields are read exactly once into locals.
  const uint32_t w = ReadOnce(sh->width);
  const uint32_t h = ReadOnce(sh->height);
  const uint32_t stride = ReadOnce(sh->stride);
  const uint32_t format = ReadOnce(sh->format);
  const int64_t ts = ReadOnce(sh->timestampQpc);
  const uint64_t frameId = ReadOnce(sh->frameId);

  if (format != kFormatNv12) return false;
  if (w != width || h != height) return false;
  if (stride < w) return false;
  const uint64_t bytes = uint64_t(stride) * h * 3 / 2;
  if (bytes > uint64_t(kSlotStride) - kSlotHeaderBytes) return false;
  // Absolute freshness: a frame older than 0.5 s (a dead writer's leftovers) is never Fresh; a timestamp more than
  // 1 s in the future is invalid.
  // Range-check before any subtraction so a hostile timestamp (INT64_MIN/MAX) cannot overflow.
  if (qpcFreq <= 0 || nowQpc < 0) return false;
  if (ts <= 0) return false;
  if (ts > nowQpc && ts - nowQpc > qpcFreq) return false;  // future by more than 1 s
  if (ts < nowQpc && nowQpc - ts > qpcFreq / 2) return false;  // older than 0.5 s
  if (hasLast_ && lastWidth_ == w && lastHeight_ == h && frameId <= lastFrameId_) return false;  // not newer

  staging_.resize(size_t(bytes));
  std::memcpy(staging_.data(), reinterpret_cast<const uint8_t*>(sh) + kSlotHeaderBytes, size_t(bytes));

  std::atomic_thread_fence(std::memory_order_acquire);
  if (testHookAfterCopy) testHookAfterCopy(slot);
  const uint64_t s2 = ReadOnce(sh->seq);
  if (s1 != s2) return false;

  stagedStride_ = stride;
  tsOut = ts;
  frameIdOut = frameId;
  return true;
}

ReadResult FrameReader::ReadImpl(const uint8_t* region, size_t regionBytes, uint32_t width, uint32_t height,
                             int64_t nowQpc, int64_t qpcFreq, uint8_t* dst, size_t dstBytes, uint32_t dstPitch) {
  auto fallback = [&]() {
    if (hasLast_ && lastWidth_ == width && lastHeight_ == height && qpcFreq > 0 &&
        std::max<int64_t>(0, nowQpc - lastQpc_) <= qpcFreq / 2) {
      return ReadResult::Reused;
    }
    return ReadResult::Stale;
  };

  if (!region || !dst || regionBytes < kMappingBytes) return ReadResult::Stale;
  if (width == 0 || height == 0 || (width & 1u) || (height & 1u) || dstPitch < width) return ReadResult::Stale;
  if (uint64_t(dstPitch) * height * 3 / 2 > dstBytes) return ReadResult::Stale;

  const ControlHeader* ch = reinterpret_cast<const ControlHeader*>(region);
  const uint32_t magic = ReadOnce(ch->magic);
  const uint32_t version = ReadOnce(ch->version);
  const uint32_t headerBytes = ReadOnce(ch->headerBytes);
  const uint32_t slotCount = ReadOnce(ch->slotCount);
  const uint32_t slotStride = ReadOnce(ch->slotStride);
  const uint32_t latest = ReadOnce(ch->latestSlot);
  const uint32_t pid = ReadOnce(ch->writerPid);
  const uint64_t generation = ReadOnce(ch->consumerGeneration);
  if (magic != kMagic || version != kVersion || headerBytes != kHeaderBytes || slotCount != kSlotCount ||
      slotStride != kSlotStride) {
    return ReadResult::Stale;
  }

  // A different writer process or a re-created mapping restarts frame numbering: forget the last good frame.
  if (hasLast_ && (pid != lastPid_ || generation != lastGeneration_)) ResetLastGood();

  // Candidates: latestSlot first, then the others in descending frameId order.
  uint32_t order[kSlotCount];
  uint32_t n = 0;
  if (latest < kSlotCount) order[n++] = latest;
  uint32_t others[kSlotCount];
  uint64_t ids[kSlotCount] = {};
  uint32_t m = 0;
  for (uint32_t i = 0; i < kSlotCount; ++i) {
    if (i == latest) continue;
    const SlotHeader* sh = reinterpret_cast<const SlotHeader*>(region + kHeaderBytes + size_t(i) * kSlotStride);
    ids[i] = ReadOnce(sh->frameId);
    others[m++] = i;
  }
  std::sort(others, others + m, [&](uint32_t a, uint32_t b) { return ids[a] > ids[b]; });
  for (uint32_t i = 0; i < m; ++i) order[n++] = others[i];

  for (uint32_t k = 0; k < n; ++k) {
    int64_t ts = 0;
    uint64_t frameId = 0;
    if (!TryReadSlot(region, order[k], width, height, nowQpc, qpcFreq, ts, frameId)) continue;

    // Success: unpack staging_ (stride-packed) into dst (dstPitch-packed).
    const uint32_t stride = stagedStride_;
    const uint8_t* srcY = staging_.data();
    const uint8_t* srcUV = srcY + size_t(stride) * height;
    for (uint32_t y = 0; y < height; ++y) {
      std::memcpy(dst + size_t(y) * dstPitch, srcY + size_t(y) * stride, width);
    }
    uint8_t* dstUV = dst + size_t(dstPitch) * height;
    for (uint32_t y = 0; y < height / 2; ++y) {
      std::memcpy(dstUV + size_t(y) * dstPitch, srcUV + size_t(y) * stride, width);
    }
    hasLast_ = true;
    lastPid_ = pid;
    lastGeneration_ = generation;
    lastFrameId_ = frameId;
    lastQpc_ = ts;
    lastWidth_ = width;
    lastHeight_ = height;
    return ReadResult::Fresh;
  }
  return fallback();
}

void FrameReader::ResetLastGood() {
  hasLast_ = false;
  lastFrameId_ = 0;
  lastQpc_ = 0;
}

ReadResult FrameReader::Read(const uint8_t* region, size_t regionBytes, uint32_t width, uint32_t height,
                             int64_t nowQpc, int64_t qpcFreq, uint8_t* dst, size_t dstBytes, uint32_t dstPitch) {
  const ReadResult r = ReadImpl(region, regionBytes, width, height, nowQpc, qpcFreq, dst, dstBytes, dstPitch);
  // Stale means the stream restarts from scratch: a restarted writer numbers frames from 1 again.
  if (r == ReadResult::Stale) ResetLastGood();
  return r;
}

}  // namespace raaicam

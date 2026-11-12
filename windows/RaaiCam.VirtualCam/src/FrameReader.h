#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <vector>

namespace raaicam {

enum class ReadResult {
  Fresh,   // a new, validated frame was copied into dst
  Reused,  // dst still holds the last good frame (<= 500 ms old); dst was not modified
  Stale    // no usable frame; caller must draw the placeholder card; dst was not modified
};

// Seqlock reader for the v2 shared mapping. One instance per stream (not thread-safe).
// dst is NV12: Y plane (dstPitch bytes per row, `height` rows) then UV plane at dst + dstPitch*height.
// A slot is only Fresh if its timestamp is at most 0.5 s older than now (and not more than 1 s in the future),
// so frames left behind by a dead writer are never replayed.
// dst is only written when the result is Fresh (pixels are staged internally first).
class FrameReader {
 public:
  ReadResult Read(const uint8_t* region, size_t regionBytes, uint32_t width, uint32_t height, int64_t nowQpc,
                  int64_t qpcFreq, uint8_t* dst, size_t dstBytes, uint32_t dstPitch);

  // Test seam: invoked after the pixel copy and before the closing seq re-read.
  std::function<void(uint32_t slot)> testHookAfterCopy;

 private:
  ReadResult ReadImpl(const uint8_t* region, size_t regionBytes, uint32_t width, uint32_t height, int64_t nowQpc,
                      int64_t qpcFreq, uint8_t* dst, size_t dstBytes, uint32_t dstPitch);
  void ResetLastGood();
  bool TryReadSlot(const uint8_t* region, uint32_t slot, uint32_t width, uint32_t height, int64_t nowQpc,
                   int64_t qpcFreq, int64_t& tsOut, uint64_t& frameIdOut);

  std::vector<uint8_t> staging_;
  uint32_t stagedStride_ = 0;
  bool hasLast_ = false;
  uint64_t lastFrameId_ = 0;
  int64_t lastQpc_ = 0;
  uint32_t lastWidth_ = 0;
  uint32_t lastHeight_ = 0;
  uint32_t lastPid_ = 0;
  uint64_t lastGeneration_ = 0;
};

}  // namespace raaicam

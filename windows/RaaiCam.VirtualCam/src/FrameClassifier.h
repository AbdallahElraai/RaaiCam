#pragma once
#include <cstddef>
#include <cstdint>

namespace raaicam {

// Pure frame-classification helpers shared by vcam-probe and vcam_tests (header-only, no Windows dependencies).
//
// A frame is "camera" (real phone video) when it is neither the placeholder card nor the TestPatternSource
// pattern AND its luma variance is above kCameraMinVariance. The variance gate only rejects flat frames
// (black, uniform, lens covered); the card is excluded by its exact signature, not by variance, because the
// card's flat 0x20 background plus a pulsing 0xA0..0xC0 square has a luma variance of about 450, which a
// dim real scene can fall below. Real indoor video has a luma stddev of tens of levels (variance in the
// thousands); a plain wall still shows sensor noise and shading well above 100 (stddev 10 levels).
constexpr double kCameraMinVariance = 100.0;

// Placeholder-card signature: corner Y == 0x20 and center Y in 0xA0..0xC0 (see DrawPlaceholderCard).
inline bool IsCardSignature(const uint8_t* y, uint32_t w, uint32_t h) {
  if (!y || w == 0 || h == 0) return false;
  const uint8_t corner = y[0];
  const uint8_t center = y[size_t(h / 2) * w + w / 2];
  return corner == 0x20 && center >= 0xA0 && center <= 0xC0;
}

// Population variance of the Y plane, sampled every 4th pixel of every 4th row (same grid as the probe's MeanY).
inline double LumaVariance(const uint8_t* y, uint32_t w, uint32_t h) {
  if (!y || w == 0 || h == 0) return 0;
  double sum = 0, sumSq = 0, n = 0;
  for (uint32_t row = 0; row < h; row += 4)
    for (uint32_t x = 0; x < w; x += 4) {
      const double v = y[size_t(row) * w + x];
      sum += v;
      sumSq += v * v;
      n += 1;
    }
  if (n == 0) return 0;
  const double mean = sum / n;
  const double var = sumSq / n - mean * mean;
  return var < 0 ? 0 : var;
}

// isCard / isLive come from the probe's existing classification; this adds the variance gate.
inline bool IsCameraFrame(bool isCard, bool isLive, double variance) {
  return !isCard && !isLive && variance > kCameraMinVariance;
}

}  // namespace raaicam

#pragma once
#include <cstdint>

namespace raaicam {

// Fills a whole NV12 frame (Y plane then UV plane at dst + pitch*height): dark background,
// centered rounded square (side = height/5) pulsing over a 2 s period (tick at 30 fps). No text.
void DrawPlaceholderCard(uint8_t* dst, uint32_t pitch, uint32_t width, uint32_t height, uint64_t tick);

}  // namespace raaicam

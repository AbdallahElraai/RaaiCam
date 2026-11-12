#include "PlaceholderCard.h"

#include <cmath>
#include <cstring>

namespace raaicam {

void DrawPlaceholderCard(uint8_t* dst, uint32_t pitch, uint32_t width, uint32_t height, uint64_t tick) {
  if (!dst || width == 0 || height == 0 || pitch < width) return;

  constexpr double kPi = 3.14159265358979323846;
  const double phase = double(tick % 60) / 60.0;  // 2 s period at 30 fps
  const int bright = 0xB0 + int(std::lround(0x10 * std::cos(2.0 * kPi * phase)));
  const uint8_t fg = uint8_t(bright);

  for (uint32_t y = 0; y < height; ++y) std::memset(dst + size_t(y) * pitch, 0x20, width);
  uint8_t* uv = dst + size_t(pitch) * height;
  for (uint32_t y = 0; y < height / 2; ++y) std::memset(uv + size_t(y) * pitch, 0x80, width);

  int side = int(height / 5);
  if (side > int(width)) side = int(width);
  if (side < 2) return;
  const int radius = side / 6;
  const int x0 = (int(width) - side) / 2;
  const int y0 = (int(height) - side) / 2;
  for (int y = 0; y < side; ++y) {
    // horizontal inset of this row from the rounded corners
    int inset = 0;
    if (y < radius || y >= side - radius) {
      const int dy = (y < radius) ? radius - y - 1 : y - (side - radius);
      const double dx = std::sqrt(double(radius) * radius - double(dy + 0.5) * (dy + 0.5));
      inset = radius - int(dx);
    }
    std::memset(dst + size_t(y0 + y) * pitch + size_t(x0 + inset), fg, size_t(side - 2 * inset));
  }
}

}  // namespace raaicam

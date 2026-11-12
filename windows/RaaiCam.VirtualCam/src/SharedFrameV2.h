// Shared-frame layout v2. Normative spec: protocol/SHARED_FRAME.md
#pragma once
#include <cstddef>
#include <cstdint>

namespace raaicam {

constexpr uint32_t kMagic = 0x32464352;  // 'RCF2'
constexpr uint32_t kVersion = 2;
constexpr uint32_t kHeaderBytes = 256;
constexpr uint32_t kSlotCount = 3;
constexpr uint32_t kSlotHeaderBytes = 64;
constexpr uint32_t kMaxFrameBytes = 1920 * 1080 * 3 / 2;
constexpr uint32_t kSlotStride = kSlotHeaderBytes + kMaxFrameBytes;
constexpr uint32_t kMappingBytes = kHeaderBytes + kSlotCount * kSlotStride;
constexpr uint32_t kFormatNv12 = 0x3231564E;  // 'NV12'
constexpr uint32_t kNoSlot = 0xFFFFFFFFu;
constexpr wchar_t kMappingName[] = L"Global\\RaaiCam.VCam.Frames.v2";

#pragma pack(push, 1)
struct ControlHeader {
  uint32_t magic;                // 0
  uint32_t version;              // 4
  uint32_t headerBytes;          // 8
  uint32_t slotCount;            // 12
  uint32_t slotStride;           // 16
  uint32_t maxWidth;             // 20
  uint32_t maxHeight;            // 24
  uint32_t _pad0;                // 28
  uint64_t consumerGeneration;   // 32 (reader)
  uint32_t consumerActive;       // 40 (reader)
  uint32_t requestedWidth;       // 44 (reader)
  uint32_t requestedHeight;      // 48 (reader)
  uint32_t _pad1;                // 52
  int64_t readerHeartbeatQpc;    // 56 (reader)
  uint32_t writerPid;            // 64 (writer)
  uint32_t latestSlot;           // 68 (writer)
  int64_t writerHeartbeatQpc;    // 72 (writer)
  uint8_t reserved[256 - 80];    // 80
};

struct SlotHeader {
  uint64_t seq;           // 0
  uint32_t width;         // 8
  uint32_t height;        // 12
  uint32_t stride;        // 16
  uint32_t format;        // 20
  int64_t timestampQpc;   // 24
  uint64_t frameId;       // 32
  uint8_t reserved[64 - 40];
};
#pragma pack(pop)

static_assert(sizeof(ControlHeader) == 256, "ControlHeader size");
static_assert(offsetof(ControlHeader, magic) == 0);
static_assert(offsetof(ControlHeader, version) == 4);
static_assert(offsetof(ControlHeader, headerBytes) == 8);
static_assert(offsetof(ControlHeader, slotCount) == 12);
static_assert(offsetof(ControlHeader, slotStride) == 16);
static_assert(offsetof(ControlHeader, maxWidth) == 20);
static_assert(offsetof(ControlHeader, maxHeight) == 24);
static_assert(offsetof(ControlHeader, consumerGeneration) == 32);
static_assert(offsetof(ControlHeader, consumerActive) == 40);
static_assert(offsetof(ControlHeader, requestedWidth) == 44);
static_assert(offsetof(ControlHeader, requestedHeight) == 48);
static_assert(offsetof(ControlHeader, readerHeartbeatQpc) == 56);
static_assert(offsetof(ControlHeader, writerPid) == 64);
static_assert(offsetof(ControlHeader, latestSlot) == 68);
static_assert(offsetof(ControlHeader, writerHeartbeatQpc) == 72);

static_assert(sizeof(SlotHeader) == kSlotHeaderBytes, "SlotHeader size");
static_assert(offsetof(SlotHeader, seq) == 0);
static_assert(offsetof(SlotHeader, width) == 8);
static_assert(offsetof(SlotHeader, height) == 12);
static_assert(offsetof(SlotHeader, stride) == 16);
static_assert(offsetof(SlotHeader, format) == 20);
static_assert(offsetof(SlotHeader, timestampQpc) == 24);
static_assert(offsetof(SlotHeader, frameId) == 32);

static_assert(kSlotStride == 64 + 3110400);

}  // namespace raaicam

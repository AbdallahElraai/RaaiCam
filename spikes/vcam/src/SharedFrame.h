// RaaiCam Phase 0 spike: shared-memory frame boundary between the virtual camera media source
// (reader, runs inside the Frame Server service) and the user-mode frame writer.
// Mirrored by spikes/framewriter/SharedFrameLayout.cs. Keep both in sync.
#pragma once

#include <cstdint>

// The DLL tries Global\ first, then Local\. Inside session 0 (Frame Server), Local\ and Global\
// resolve to the same namespace, so a writer in the user session always opens Global\.
#define RAAICAM_MAPPING_NAME_GLOBAL L"Global\\RaaiCamSpikeFrames"
#define RAAICAM_MAPPING_NAME_LOCAL  L"Local\\RaaiCamSpikeFrames"
#define RAAICAM_EVENT_NAME_GLOBAL   L"Global\\RaaiCamSpikeFrameReady"  // auto-reset
#define RAAICAM_EVENT_NAME_LOCAL    L"Local\\RaaiCamSpikeFrameReady"

// System, LocalService, Interactive Users: generic all.
#define RAAICAM_MAPPING_SDDL L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;IU)"

constexpr uint32_t RAAICAM_MAGIC = 0x52434631;   // 'RCF1'
constexpr uint32_t RAAICAM_VERSION = 1;
constexpr uint32_t RAAICAM_FORMAT_NV12 = 0x3231564E; // 'NV12'
constexpr uint32_t RAAICAM_MAX_WIDTH = 1920;
constexpr uint32_t RAAICAM_MAX_HEIGHT = 1080;

#pragma pack(push, 1)
struct SharedFrameHeader
{
	uint32_t magic;          // RAAICAM_MAGIC
	uint32_t version;        // RAAICAM_VERSION
	uint32_t width;          // set by the reader (camera) to the size the consumer selected
	uint32_t height;
	uint32_t format;         // RAAICAM_FORMAT_NV12
	uint32_t stride;         // bytes per Y row (== width for the spike)
	uint64_t frameCounter;   // incremented by the writer AFTER the frame is copied
	int64_t timestampQpc;    // writer QueryPerformanceCounter at write time; reader treats frames older than 500 ms as stale
	uint32_t consumerActive; // 1 while a camera stream is started, else 0 (set by the reader)
	uint32_t reserved[7];
};
#pragma pack(pop)

static_assert(sizeof(SharedFrameHeader) == 72, "SharedFrameHeader must be 72 bytes");

constexpr uint32_t RAAICAM_HEADER_SIZE = sizeof(SharedFrameHeader);
constexpr uint32_t RAAICAM_MAX_FRAME_BYTES = RAAICAM_MAX_WIDTH * RAAICAM_MAX_HEIGHT * 3 / 2;
constexpr uint32_t RAAICAM_MAPPING_SIZE = RAAICAM_HEADER_SIZE + RAAICAM_MAX_FRAME_BYTES;

// RaaiCam Phase 0 spike: shared-memory reader + "no signal" fallback pattern.
#pragma once

#include <windows.h>
#include <mfobjects.h>
#include "SharedFrame.h"

class FrameSource
{
public:
	static FrameSource& Instance();

	// Fills the (allocator-provided) NV12 sample with the latest shared frame if the writer
	// advanced frameCounter within the last 500 ms, else with the "no signal" pattern.
	// Deviation from the brief's GetFrame(IMFSample** out, ...): Frame Server hands the stream
	// a sample allocator, so the stream allocates and FrameSource fills.
	HRESULT GetFrame(IMFSample* sample, UINT32 width, UINT32 height);

	// Called on stream start/stop. Publishes width/height/stride/format and consumerActive.
	void StreamStarted(UINT32 width, UINT32 height);
	void StreamStopped();

private:
	FrameSource();
	bool EnsureMapping();      // caller holds _lock
	void FillPattern(BYTE* y, LONG pitch, BYTE* uv, UINT32 width, UINT32 height);

	SRWLOCK _lock = SRWLOCK_INIT;
	HANDLE _mapping = nullptr;
	HANDLE _event = nullptr;
	SharedFrameHeader* _header = nullptr;
	BYTE* _frame = nullptr;
	ULONGLONG _lastAttemptTick = 0;
	bool _attempted = false;

	uint64_t _lastCounter = 0;
	LONGLONG _lastChangeQpc = 0;
	LONGLONG _qpcFreq = 1;
	int _blockX = 0;
	int _activeStreams = 0;
	int _lastMode = -1;        // 0 = pattern, 1 = live; for transition logging
	uint64_t _framesServed = 0;
};

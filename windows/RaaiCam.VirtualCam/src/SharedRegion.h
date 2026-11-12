// Process-wide owner of the v2 shared mapping (protocol/SHARED_FRAME.md), reader (DLL) side.
//
// It owns only the mapping and the reader-written ControlHeader fields. Per-stream frame state
// (FrameReader, card tick, staging buffer) lives in each MediaStream.
//
// Locking: one internal SRW lock, a LEAF lock. It is never held across I/O (registry, CreateFileMapping,
// MapViewOfFile, logging) and nothing is called out while it is held, so it may be taken while a stream
// lock is held. Order: source lock -> stream lock -> SharedRegion lock -> log lock.
#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>

#include "ActiveStreamSet.h"

namespace raaicam {

class SharedRegion {
 public:
  static SharedRegion& Instance();

  // Returns the mapped view (kMappingBytes), or nullptr while the mapping is unavailable.
  // Lock-free once mapped. Otherwise at most one attempt every 2 s (process-wide): reads
  // AllowedUserSid, builds the DACL, creates or opens the mapping, initializes a brand-new header or
  // validates an existing one, and bumps consumerGeneration. Returns nullptr (card) while AllowedUserSid
  // is missing/invalid or the existing object is foreign. When the mapping becomes available while
  // streams are active, the consumer state is published immediately ("late mapping" fix).
  // Does I/O on the attempt: call it WITHOUT holding a stream lock.
  uint8_t* TryAcquire();

  // Records and (if mapped) writes consumerActive + requestedWidth/Height. Normally reached through
  // OnStreamStarted/OnStreamStopped; re-applied by TryAcquire when the mapping appears late.
  void PublishConsumer(bool active, uint32_t width, uint32_t height);

  // Active-stream bookkeeping, keyed by stream identity. Every start and stop republishes the size of the
  // most recently started stream that is still active; (0, 0, 0) when none is.
  void OnStreamStarted(const void* stream, uint32_t width, uint32_t height);
  void OnStreamStopped(const void* stream);

  // readerHeartbeatQpc = qpc (no-op while unmapped). Lock-free.
  void Heartbeat(int64_t qpc);

  // Unmaps and closes. Only when no stream exists (DllCanUnloadNow returned S_OK).
  void Release();

  // Constant-initialized, trivially destructible (no work at DLL load/unload). Use Instance().
  constexpr SharedRegion() = default;

 private:
  enum class Status { None, Ok, NoSid, BadSid, SddlFailed, CreateFailed, MapFailed, Foreign };
  struct Attempt {
    Status status = Status::None;
    HANDLE mapping = nullptr;
    uint8_t* view = nullptr;
    DWORD error = 0;
    bool created = false;
    uint64_t generation = 0;
  };

  static Attempt CreateAndMap();
  void WriteConsumerLocked();
  void PublishConsumerLocked(bool active, uint32_t width, uint32_t height);

  SRWLOCK lock_ = SRWLOCK_INIT;
  std::atomic<uint8_t*> view_{nullptr};
  HANDLE mapping_ = nullptr;
  bool attempted_ = false;
  bool acquiring_ = false;
  ULONGLONG lastAttemptTick_ = 0;
  Status lastStatus_ = Status::None;
  DWORD lastError_ = 0;
  ActiveStreamSet streams_;
  bool desiredActive_ = false;
  uint32_t desiredWidth_ = 0;
  uint32_t desiredHeight_ = 0;
};

}  // namespace raaicam

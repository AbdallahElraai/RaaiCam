// Reader-side owner of the v2 shared mapping: see SharedRegion.h and protocol/SHARED_FRAME.md.
#include "SharedRegion.h"

#include <sddl.h>

#include <string>

#include "Log.h"
#include "Security.h"
#include "SharedFrameV2.h"

namespace raaicam {

namespace {

constexpr ULONGLONG kRetryMs = 2000;

// The singleton has a constexpr constructor and a trivial destructor: constant initialization, and no
// atexit destructor running under the loader lock at DLL unload.
SharedRegion g_region;

template <typename T>
T LoadOnce(const T& v) {
  return *reinterpret_cast<const volatile T*>(&v);
}

template <typename T>
void StoreOnce(T& v, T value) {
  *reinterpret_cast<volatile T*>(&v) = value;
}

ControlHeader* Header(uint8_t* view) { return reinterpret_cast<ControlHeader*>(view); }

}  // namespace

SharedRegion& SharedRegion::Instance() { return g_region; }

// No locks held: registry read, DACL build, CreateFileMappingW, MapViewOfFile and header init/validation.
SharedRegion::Attempt SharedRegion::CreateAndMap() {
  Attempt a;
  std::wstring sddl;
  try {
    const auto sid = ReadAllowedUserSid();
    if (!sid) {
      a.status = Status::NoSid;
      return a;
    }
    sddl = BuildMappingSddl(*sid);
  } catch (...) {
    a.status = Status::SddlFailed;
    a.error = ERROR_OUTOFMEMORY;
    return a;
  }
  if (sddl.empty()) {
    a.status = Status::BadSid;
    return a;
  }

  PSECURITY_DESCRIPTOR sd = nullptr;
  if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) {
    a.status = Status::SddlFailed;
    a.error = GetLastError();
    return a;
  }
  SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
  SetLastError(ERROR_SUCCESS);
  HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, kMappingBytes, kMappingName);
  const DWORD createError = GetLastError();
  LocalFree(sd);
  if (!mapping) {
    a.status = Status::CreateFailed;
    a.error = createError;
    return a;
  }
  const bool existed = createError == ERROR_ALREADY_EXISTS;

  // Fails if an existing section with this name is smaller than the v2 layout.
  void* view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, kMappingBytes);
  if (!view) {
    a.error = GetLastError();
    a.status = existed ? Status::Foreign : Status::MapFailed;
    CloseHandle(mapping);
    return a;
  }
  ControlHeader* h = Header(static_cast<uint8_t*>(view));

  // magic is written last when we initialize, so magic == 0 on an existing object means a previous
  // instance of this DLL died between create and init: no writer can have used it, initialize it.
  const uint32_t magic = LoadOnce(h->magic);
  if (!existed || magic == 0) {
    // Brand-new mapping (zero-filled by the system): initialize the ControlHeader before anything is
    // published; magic goes last behind a full barrier so a writer never sees a half-built header.
    const uint64_t oldGen = existed ? LoadOnce(h->consumerGeneration) : 0;
    StoreOnce(h->version, kVersion);
    StoreOnce(h->headerBytes, kHeaderBytes);
    StoreOnce(h->slotCount, kSlotCount);
    StoreOnce(h->slotStride, kSlotStride);
    StoreOnce(h->maxWidth, uint32_t(1920));
    StoreOnce(h->maxHeight, uint32_t(1080));
    const uint64_t gen = oldGen + 1 == 0 ? 1 : oldGen + 1;
    StoreOnce(h->consumerGeneration, gen);
    StoreOnce(h->consumerActive, uint32_t(0));
    StoreOnce(h->requestedWidth, uint32_t(0));
    StoreOnce(h->requestedHeight, uint32_t(0));
    StoreOnce(h->readerHeartbeatQpc, int64_t(0));
    StoreOnce(h->latestSlot, kNoSlot);
    MemoryBarrier();
    StoreOnce(h->magic, kMagic);
    MemoryBarrier();
    a.created = true;
    a.generation = gen;
  } else {
    // Existing object (Frame Server restarted while the app kept its handle): keep the contents,
    // validate the layout, then bump consumerGeneration (never zero it) so the writer resynchronizes.
    const bool valid = magic == kMagic && LoadOnce(h->version) == kVersion &&
                       LoadOnce(h->headerBytes) == kHeaderBytes && LoadOnce(h->slotCount) == kSlotCount &&
                       LoadOnce(h->slotStride) == kSlotStride && LoadOnce(h->maxWidth) == 1920u &&
                       LoadOnce(h->maxHeight) == 1080u;
    if (!valid) {
      // Foreign object squatting the name: never write to it, serve the card.
      UnmapViewOfFile(view);
      CloseHandle(mapping);
      a.status = Status::Foreign;
      return a;
    }
    auto* gen = reinterpret_cast<volatile LONG64*>(&h->consumerGeneration);
    LONG64 next = InterlockedIncrement64(gen);
    if (next == 0) next = InterlockedIncrement64(gen);
    a.generation = uint64_t(next);
  }

  a.status = Status::Ok;
  a.mapping = mapping;
  a.view = static_cast<uint8_t*>(view);
  return a;
}

uint8_t* SharedRegion::TryAcquire() {
  if (uint8_t* v = view_.load(std::memory_order_acquire)) return v;

  AcquireSRWLockExclusive(&lock_);
  if (uint8_t* v = view_.load(std::memory_order_acquire)) {
    ReleaseSRWLockExclusive(&lock_);
    return v;
  }
  const ULONGLONG now = GetTickCount64();
  if (acquiring_ || (attempted_ && now - lastAttemptTick_ < kRetryMs)) {
    ReleaseSRWLockExclusive(&lock_);
    return nullptr;
  }
  attempted_ = true;
  acquiring_ = true;
  lastAttemptTick_ = now;
  ReleaseSRWLockExclusive(&lock_);

  const Attempt a = CreateAndMap();  // I/O, no lock held

  AcquireSRWLockExclusive(&lock_);
  acquiring_ = false;
  // Log only when the outcome changes (no per-attempt spam while e.g. the SID stays missing).
  const bool changed = a.status != lastStatus_ || (a.status != Status::Ok && a.error != lastError_);
  lastStatus_ = a.status;
  lastError_ = a.error;
  bool published = false;
  if (a.status == Status::Ok) {
    mapping_ = a.mapping;
    view_.store(a.view, std::memory_order_release);
    // Always overwrite the consumer fields with our real state: an existing mapping may still carry
    // consumerActive=1 from a Frame Server instance that died. This is also the late-mapping fix.
    WriteConsumerLocked();
    published = desiredActive_;
  }
  const bool active = desiredActive_;
  const uint32_t w = desiredWidth_, hgt = desiredHeight_;
  ReleaseSRWLockExclusive(&lock_);

  if (changed || a.status == Status::Ok) {
    switch (a.status) {
      case Status::Ok:
        VLog(L"SharedRegion: mapping %s (%s), consumerGeneration=%llu, consumer %s%s", kMappingName,
             a.created ? L"created" : L"opened existing", a.generation, active ? L"active" : L"inactive",
             published ? L" (published)" : L"");
        if (published) VLog(L"SharedRegion: published consumer %ux%u", w, hgt);
        break;
      case Status::NoSid:
        VLog(L"SharedRegion: HKLM\\SOFTWARE\\RaaiCam\\VirtualCam\\AllowedUserSid missing; not creating the "
             L"mapping, serving the card (retry every 2 s)");
        break;
      case Status::BadSid:
        VLog(L"SharedRegion: AllowedUserSid is not a valid SID; not creating the mapping, serving the card");
        break;
      case Status::SddlFailed:
        VLogWin32(L"SharedRegion: building the mapping security descriptor", a.error);
        break;
      case Status::CreateFailed:
        VLogWin32(L"SharedRegion: CreateFileMappingW", a.error);
        break;
      case Status::MapFailed:
        VLogWin32(L"SharedRegion: MapViewOfFile", a.error);
        break;
      case Status::Foreign:
        VLog(L"SharedRegion: existing object %s has an unexpected layout (error %lu); not touching it, serving "
             L"the card",
             kMappingName, a.error);
        break;
      case Status::None:
        break;
    }
  }
  return a.view;
}

// Caller holds lock_. Sizes before active=1; active=0 before sizes are cleared.
void SharedRegion::WriteConsumerLocked() {
  uint8_t* v = view_.load(std::memory_order_relaxed);
  if (!v) return;
  ControlHeader* h = Header(v);
  if (desiredActive_) {
    StoreOnce(h->requestedWidth, desiredWidth_);
    StoreOnce(h->requestedHeight, desiredHeight_);
    MemoryBarrier();
    StoreOnce(h->consumerActive, uint32_t(1));
  } else {
    StoreOnce(h->consumerActive, uint32_t(0));
    MemoryBarrier();
    StoreOnce(h->requestedWidth, uint32_t(0));
    StoreOnce(h->requestedHeight, uint32_t(0));
  }
  MemoryBarrier();
}

void SharedRegion::PublishConsumer(bool active, uint32_t width, uint32_t height) {
  AcquireSRWLockExclusive(&lock_);
  PublishConsumerLocked(active, width, height);
  ReleaseSRWLockExclusive(&lock_);
}

// Caller holds lock_.
void SharedRegion::PublishConsumerLocked(bool active, uint32_t width, uint32_t height) {
  desiredActive_ = active;
  desiredWidth_ = active ? width : 0;
  desiredHeight_ = active ? height : 0;
  WriteConsumerLocked();
}

// Set update and publish happen in ONE critical section, so a concurrent stop/start pair can never
// publish out of order.
void SharedRegion::OnStreamStarted(const void* stream, uint32_t width, uint32_t height) {
  AcquireSRWLockExclusive(&lock_);
  streams_.Start(stream, width, height);
  uint32_t w = 0, h = 0;
  const bool active = streams_.Current(w, h);
  PublishConsumerLocked(active, w, h);
  ReleaseSRWLockExclusive(&lock_);
}

void SharedRegion::OnStreamStopped(const void* stream) {
  AcquireSRWLockExclusive(&lock_);
  streams_.Stop(stream);
  uint32_t w = 0, h = 0;
  const bool active = streams_.Current(w, h);
  PublishConsumerLocked(active, w, h);
  ReleaseSRWLockExclusive(&lock_);
}

void SharedRegion::Heartbeat(int64_t qpc) {
  uint8_t* v = view_.load(std::memory_order_acquire);
  if (!v) return;
  InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&Header(v)->readerHeartbeatQpc), qpc);
}

void SharedRegion::Release() {
  AcquireSRWLockExclusive(&lock_);
  uint8_t* v = view_.exchange(nullptr, std::memory_order_acq_rel);
  HANDLE m = mapping_;
  mapping_ = nullptr;
  attempted_ = false;
  lastStatus_ = Status::None;
  lastError_ = 0;
  ReleaseSRWLockExclusive(&lock_);
  if (v) UnmapViewOfFile(v);
  if (m) CloseHandle(m);
}

}  // namespace raaicam

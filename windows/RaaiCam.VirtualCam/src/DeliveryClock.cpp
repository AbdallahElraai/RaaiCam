// 30 fps threadpool delivery clock: see DeliveryClock.h.
#include "DeliveryClock.h"

namespace raaicam {

namespace {
constexpr int64_t kFps = 30;
}

DeliveryClock::~DeliveryClock() { Stop(); }

HRESULT DeliveryClock::Create(TickFn fn, void* context, HMODULE module) {
  if (!fn || timer_) return E_UNEXPECTED;
  LARGE_INTEGER f{};
  QueryPerformanceFrequency(&f);
  if (f.QuadPart <= 0) return E_UNEXPECTED;

  InitializeThreadpoolEnvironment(&env_);
  // Keeps this DLL loaded while callbacks are pending or running.
  if (module) SetThreadpoolCallbackLibrary(&env_, module);
  PTP_TIMER t = CreateThreadpoolTimer(&DeliveryClock::OnTimer, this, &env_);
  if (!t) {
    const HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
    DestroyThreadpoolEnvironment(&env_);
    return FAILED(hr) ? hr : E_FAIL;
  }

  AcquireSRWLockExclusive(&lock_);
  fn_ = fn;
  context_ = context;
  qpcFreq_ = f.QuadPart;
  periodQpc_ = f.QuadPart / kFps;
  tickIndex_ = 0;
  stopping_ = false;
  timer_ = t;
  ReleaseSRWLockExclusive(&lock_);
  return S_OK;
}

void DeliveryClock::Arm() {
  LARGE_INTEGER now{};
  QueryPerformanceCounter(&now);
  AcquireSRWLockExclusive(&lock_);
  if (timer_ && !stopping_) {
    epochQpc_ = now.QuadPart;
    tickIndex_ = 0;
    ArmLocked(now.QuadPart);
  }
  ReleaseSRWLockExclusive(&lock_);
}

// Caller holds lock_. Arms a one-shot for tick (tickIndex_ + 1) of the absolute schedule.
void DeliveryClock::ArmLocked(int64_t nowQpc) {
  int64_t due = epochQpc_ + int64_t(((tickIndex_ + 1) * uint64_t(qpcFreq_)) / kFps);
  if (nowQpc - due > 2 * periodQpc_) {
    // Stalled (debugger, suspended service, overloaded pool): restart the schedule, no burst.
    epochQpc_ = nowQpc;
    tickIndex_ = 0;
    due = nowQpc + periodQpc_;
  }
  int64_t rel100ns = due > nowQpc ? ((due - nowQpc) * 10'000'000) / qpcFreq_ : 0;
  if (rel100ns < 1) rel100ns = 1;
  ULARGE_INTEGER u;
  u.QuadPart = uint64_t(-rel100ns);  // negative = relative due time
  FILETIME ft;
  ft.dwLowDateTime = u.LowPart;
  ft.dwHighDateTime = u.HighPart;
  SetThreadpoolTimer(timer_, &ft, 0, 0);
}

void CALLBACK DeliveryClock::OnTimer(PTP_CALLBACK_INSTANCE, PVOID context, PTP_TIMER) {
  auto* self = static_cast<DeliveryClock*>(context);
  LARGE_INTEGER now{};
  QueryPerformanceCounter(&now);

  AcquireSRWLockShared(&self->lock_);
  const bool stopping = self->stopping_;
  const TickFn fn = self->fn_;
  void* ctx = self->context_;
  ReleaseSRWLockShared(&self->lock_);
  if (stopping) return;

  // The tick runs with no clock lock held; it takes the stream lock itself.
  fn(ctx, now.QuadPart);

  // Re-arm after the tick (never two concurrent ticks for one clock). Under lock_ against Stop():
  // once Stop() has set stopping_ and cancelled, nothing re-arms the timer.
  AcquireSRWLockExclusive(&self->lock_);
  if (!self->stopping_ && self->timer_) {
    self->tickIndex_++;
    LARGE_INTEGER after{};
    QueryPerformanceCounter(&after);
    self->ArmLocked(after.QuadPart);
  }
  ReleaseSRWLockExclusive(&self->lock_);
}

void DeliveryClock::Stop() {
  AcquireSRWLockExclusive(&lock_);
  PTP_TIMER t = timer_;
  stopping_ = true;
  if (t) SetThreadpoolTimer(t, nullptr, 0, 0);  // cancel pending expirations
  ReleaseSRWLockExclusive(&lock_);
  if (!t) return;

  // Outside every lock: waits for a running callback (which may be blocked on the stream lock).
  WaitForThreadpoolTimerCallbacks(t, TRUE);
  CloseThreadpoolTimer(t);
  DestroyThreadpoolEnvironment(&env_);

  AcquireSRWLockExclusive(&lock_);
  timer_ = nullptr;
  fn_ = nullptr;
  context_ = nullptr;
  ReleaseSRWLockExclusive(&lock_);
}

}  // namespace raaicam

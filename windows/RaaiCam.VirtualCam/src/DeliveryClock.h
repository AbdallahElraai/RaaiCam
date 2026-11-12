// 30 fps delivery clock: one threadpool timer per started stream.
//
// The timer is a one-shot re-armed on every tick against an absolute QPC schedule (period 1/30 s =
// 33.333 ms), so the average rate is exactly 30 fps even though threadpool timers take whole
// milliseconds; after a stall longer than two periods the schedule restarts instead of bursting.
//
// Lifetime contract (the owner, MediaStream, implements it):
// - Create()/Arm() are called with the stream lock held (they only create and arm the timer: non-blocking).
// - Stop() MUST be called WITHOUT the stream lock: it cancels the timer (SetThreadpoolTimer(t, nullptr, 0, 0)),
//   waits for running callbacks (WaitForThreadpoolTimerCallbacks(t, TRUE)) and closes it. The callback takes
//   the stream lock, so waiting with that lock held would deadlock.
// - The owner keeps itself alive (strong reference) from Create() until Stop() has returned, so a callback
//   never runs on a freed object; the callback environment also pins this DLL while callbacks are pending.
//
// The clock's own lock only guards "re-arm" against "cancel"; it is a leaf lock and never held while
// calling the tick function.
#pragma once

#include <windows.h>

#include <cstdint>

namespace raaicam {

class DeliveryClock {
 public:
  using TickFn = void (*)(void* context, int64_t tickQpc);

  DeliveryClock() = default;
  ~DeliveryClock();  // Stop(): must not run under the stream lock either
  DeliveryClock(const DeliveryClock&) = delete;
  DeliveryClock& operator=(const DeliveryClock&) = delete;

  // Creates the (unarmed) timer. Can fail; nothing runs until Arm().
  HRESULT Create(TickFn fn, void* context, HMODULE module);
  // Arms it: first tick one period from now. Cannot fail.
  void Arm();
  // Cancels, waits for in-flight callbacks, closes. Idempotent. Never call from the tick itself.
  void Stop();

 private:
  static void CALLBACK OnTimer(PTP_CALLBACK_INSTANCE instance, PVOID context, PTP_TIMER timer);
  void ArmLocked(int64_t nowQpc);

  SRWLOCK lock_ = SRWLOCK_INIT;
  PTP_TIMER timer_ = nullptr;
  TP_CALLBACK_ENVIRON env_{};
  bool stopping_ = false;
  TickFn fn_ = nullptr;
  void* context_ = nullptr;
  int64_t qpcFreq_ = 0;
  int64_t periodQpc_ = 0;
  int64_t epochQpc_ = 0;
  uint64_t tickIndex_ = 0;
};

}  // namespace raaicam

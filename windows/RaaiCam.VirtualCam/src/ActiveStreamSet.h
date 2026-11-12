// Pure bookkeeping for SharedRegion: the (width, height) of every started stream, so the published
// requestedWidth/Height can follow the most recently started stream that is still active.
#pragma once

#include <cstddef>
#include <cstdint>

namespace raaicam {

class ActiveStreamSet {
 public:
  static constexpr size_t kMax = 16;

  // Registers (or re-registers) a started stream; it becomes the most recent one. When full, the oldest
  // entry is replaced (a bounded-state fallback; a source has only a handful of streams).
  void Start(const void* key, uint32_t width, uint32_t height) {
    Entry* slot = nullptr;
    for (Entry& e : e_) {
      if (e.key == key) {
        slot = &e;
        break;
      }
    }
    if (!slot) {
      for (Entry& e : e_) {
        if (!e.key) {
          slot = &e;
          break;
        }
      }
    }
    if (!slot) {
      slot = &e_[0];
      for (Entry& e : e_) {
        if (e.seq < slot->seq) slot = &e;
      }
    }
    slot->key = key;
    slot->width = width;
    slot->height = height;
    slot->seq = next_++;
  }

  // Removes a stream (no-op for an unknown key).
  void Stop(const void* key) {
    for (Entry& e : e_) {
      if (e.key == key) e = Entry{};
    }
  }

  // The most recently started stream still active. False when none is active.
  bool Current(uint32_t& width, uint32_t& height) const {
    const Entry* best = nullptr;
    for (const Entry& e : e_) {
      if (e.key && (!best || e.seq > best->seq)) best = &e;
    }
    if (!best) return false;
    width = best->width;
    height = best->height;
    return true;
  }

 private:
  struct Entry {
    const void* key = nullptr;
    uint32_t width = 0;
    uint32_t height = 0;
    uint64_t seq = 0;
  };
  Entry e_[kMax];
  uint64_t next_ = 1;
};

}  // namespace raaicam

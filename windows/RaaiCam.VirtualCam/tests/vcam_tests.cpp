#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include <doctest/doctest.h>

#include <cstring>
#include <vector>

#include "ActiveStreamSet.h"
#include "FrameReader.h"
#include "PlaceholderCard.h"
#include "Security.h"
#include "SharedFrameV2.h"

using namespace raaicam;

namespace {

constexpr int64_t kFreq = 10'000'000;  // 10 MHz QPC
constexpr int64_t kMs = kFreq / 1000;

// Plays the writer's role the way VirtualCameraSink does (seq odd -> fields+pixels -> seq even).
struct Region {
  std::vector<uint8_t> mem = std::vector<uint8_t>(kMappingBytes);

  Region() {
    auto* c = Ctl();
    c->magic = kMagic;
    c->version = kVersion;
    c->headerBytes = kHeaderBytes;
    c->slotCount = kSlotCount;
    c->slotStride = kSlotStride;
    c->maxWidth = 1920;
    c->maxHeight = 1080;
    c->consumerGeneration = 1;
    c->latestSlot = 0xFFFFFFFFu;
  }
  ControlHeader* Ctl() { return reinterpret_cast<ControlHeader*>(mem.data()); }
  uint8_t* SlotPtr(int i) { return mem.data() + kHeaderBytes + size_t(i) * kSlotStride; }
  SlotHeader* Hdr(int i) { return reinterpret_cast<SlotHeader*>(SlotPtr(i)); }

  void Write(int slot, uint32_t w, uint32_t h, uint32_t stride, uint8_t y, uint8_t uv, uint64_t frameId,
             int64_t ts, bool publish = true, uint32_t format = kFormatNv12) {
    SlotHeader* s = Hdr(slot);
    s->seq = s->seq + 1;  // odd
    s->width = w;
    s->height = h;
    s->stride = stride;
    s->format = format;
    s->timestampQpc = ts;
    s->frameId = frameId;
    size_t ySize = size_t(stride) * h;
    size_t total = ySize * 3 / 2;
    if (total <= size_t(kSlotStride) - kSlotHeaderBytes) {
      uint8_t* px = SlotPtr(slot) + kSlotHeaderBytes;
      std::memset(px, y, ySize);
      std::memset(px + ySize, uv, total - ySize);
    }
    s->seq = s->seq + 1;  // even
    if (publish) {
      Ctl()->latestSlot = uint32_t(slot);
      Ctl()->writerHeartbeatQpc = ts;
    }
  }
};

struct Dst {
  uint32_t w, h, pitch;
  std::vector<uint8_t> buf;
  Dst(uint32_t w_, uint32_t h_, uint32_t p = 0)
      : w(w_), h(h_), pitch(p ? p : w_), buf(size_t(pitch ? pitch : w_) * h_ * 3 / 2, 0xEE) {}
  uint8_t Y(uint32_t x, uint32_t y) const { return buf[size_t(y) * pitch + x]; }
  uint8_t UV(uint32_t x, uint32_t y) const { return buf[size_t(pitch) * h + size_t(y) * pitch + x]; }
};

ReadResult DoRead(FrameReader& r, Region& reg, Dst& d, int64_t now, uint32_t w = 1280, uint32_t h = 720) {
  return r.Read(reg.mem.data(), reg.mem.size(), w, h, now, kFreq, d.buf.data(), d.buf.size(), d.pitch);
}

}  // namespace

TEST_CASE("Layout_MatchesContract") {
  CHECK(kMappingBytes == 256u + 3u * (64u + 3110400u));
  CHECK(kSlotStride == 64u + 3110400u);
  CHECK(kMagic == 0x32464352u);
}

TEST_CASE("Reader_FreshFrameCopied") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x90, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x50);
  CHECK(d.Y(1279, 719) == 0x50);
  CHECK(d.UV(0, 0) == 0x90);
  CHECK(d.UV(1279, 359) == 0x90);
}

TEST_CASE("Reader_FreshFrameRepacksStrideToPitch") {
  Region reg;
  reg.Write(0, 1280, 720, 1344, 0x51, 0x91, 1, 1000);
  FrameReader r;
  Dst d(1280, 720, 1408);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(d.Y(1279, 719) == 0x51);
  CHECK(d.UV(1279, 359) == 0x91);
}

TEST_CASE("Reader_IgnoresOddSequenceSlot") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x60, 0x80, 1, 1000);
  reg.Write(1, 1280, 720, 1280, 0x70, 0x80, 2, 2000);
  reg.Hdr(1)->seq = reg.Hdr(1)->seq + 1;  // latest slot now mid-write (odd)
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 2000) == ReadResult::Fresh);
  CHECK(d.Y(10, 10) == 0x60);
}

TEST_CASE("Reader_RejectsSizeMismatch") {
  Region reg;
  reg.Write(0, 1920, 1080, 1920, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Stale);
  CHECK(d.Y(0, 0) == 0xEE);  // dst untouched
}

TEST_CASE("Reader_RejectsOversizedStride") {
  Region reg;
  reg.Write(0, 1280, 720, 4096, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Stale);
  CHECK(d.Y(0, 0) == 0xEE);
}

TEST_CASE("Reader_RejectsBadFormatOrMagic") {
  {
    Region reg;
    reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000, true, 0x12345678);
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, 1000) == ReadResult::Stale);
  }
  {
    Region reg;
    reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
    reg.Ctl()->magic = 0xDEADBEEF;
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, 1000) == ReadResult::Stale);
  }
}

TEST_CASE("Reader_ReusedWithin500ms") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(DoRead(r, reg, d, 1000 + 400 * kMs) == ReadResult::Reused);
  CHECK(d.Y(5, 5) == 0x50);
}

TEST_CASE("Reader_StaleAfter500ms") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(DoRead(r, reg, d, 1000 + 600 * kMs) == ReadResult::Stale);
}

TEST_CASE("Reader_SameFrameIdIsReused") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 7, 1000);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(DoRead(r, reg, d, 1000 + 33 * kMs) == ReadResult::Reused);
}

TEST_CASE("Reader_NewerFrameIsFreshAndTornReadKeepsDst") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  reg.Write(1, 1280, 720, 1280, 0x66, 0x80, 2, 2000);
  CHECK(DoRead(r, reg, d, 2000) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x66);
  // writer begins rewriting slot 1 (odd seq): reader must not touch dst
  reg.Hdr(1)->seq = reg.Hdr(1)->seq + 1;
  reg.Hdr(1)->frameId = 3;
  CHECK(DoRead(r, reg, d, 2000 + 33 * kMs) == ReadResult::Reused);
  CHECK(d.Y(0, 0) == 0x66);
}

TEST_CASE("Dacl_ContainsOnlyAllowedPrincipals") {
  CHECK(BuildMappingSddl(L"S-1-5-21-1-2-3-1001") == L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;S-1-5-21-1-2-3-1001)");
  CHECK(BuildMappingSddl(L"not-a-sid").empty());
  CHECK(BuildMappingSddl(L"WD").empty());  // well-known aliases (Everyone) must not be injectable
  CHECK(BuildMappingSddl(L"").empty());
}

TEST_CASE("Card_FillsWholeFrameAndPulses") {
  const uint32_t w = 1280, h = 720;
  std::vector<uint8_t> a(size_t(w) * h * 3 / 2), b(a.size());
  DrawPlaceholderCard(a.data(), w, w, h, 0);
  DrawPlaceholderCard(b.data(), w, w, h, 30);
  CHECK(a[0] == 0x20);
  CHECK(a[size_t(h - 1) * w + (w - 1)] == 0x20);
  uint8_t ca = a[size_t(h / 2) * w + w / 2], cb = b[size_t(h / 2) * w + w / 2];
  CHECK(ca >= 0xA0);
  CHECK(ca <= 0xC0);
  CHECK(cb >= 0xA0);
  CHECK(cb <= 0xC0);
  CHECK(ca != cb);
  bool uvOk = true;
  for (size_t i = size_t(w) * h; i < a.size(); ++i) uvOk = uvOk && a[i] == 0x80;
  CHECK(uvOk);
}

TEST_CASE("Reader_WriterRestartAfterStale") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1000, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(DoRead(r, reg, d, 1000 + 600 * kMs) == ReadResult::Stale);
  // writer restarted (same pid reused), numbering from 1 again
  reg.Write(1, 1280, 720, 1280, 0x77, 0x80, 1, 1000 + 700 * kMs);
  CHECK(DoRead(r, reg, d, 1000 + 700 * kMs) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x77);
}

TEST_CASE("Reader_StaleFrameDoesNotComeBackAsFresh") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1000, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  REQUIRE(DoRead(r, reg, d, 1000 + 600 * kMs) == ReadResult::Stale);
  CHECK(DoRead(r, reg, d, 1000 + 633 * kMs) == ReadResult::Stale);
  CHECK(DoRead(r, reg, d, 1000 + 666 * kMs) == ReadResult::Stale);
}

TEST_CASE("Reader_NewWriterPidAcceptedImmediately") {
  Region reg;
  reg.Ctl()->writerPid = 111;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1000, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  reg.Ctl()->writerPid = 222;
  reg.Write(1, 1280, 720, 1280, 0x66, 0x80, 1, 1000 + 50 * kMs);
  CHECK(DoRead(r, reg, d, 1000 + 100 * kMs) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x66);
}

TEST_CASE("Reader_NewGenerationAcceptedImmediately") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1000, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  reg.Ctl()->consumerGeneration = 2;
  reg.Write(1, 1280, 720, 1280, 0x66, 0x80, 1, 1000 + 50 * kMs);
  CHECK(DoRead(r, reg, d, 1000 + 100 * kMs) == ReadResult::Fresh);
}

TEST_CASE("Reader_SeqChangedDuringCopyIsRejected") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  r.testHookAfterCopy = [&](uint32_t slot) { reg.Hdr(int(slot))->seq = reg.Hdr(int(slot))->seq + 2; };
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Stale);
  CHECK(d.Y(0, 0) == 0xEE);  // torn copy never reaches dst
  // once the writer is stable the same slot is readable
  r.testHookAfterCopy = nullptr;
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
}

TEST_CASE("Reader_SeqChangeAfterGoodReadKeepsDst") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  reg.Write(1, 1280, 720, 1280, 0x66, 0x80, 2, 2000);
  r.testHookAfterCopy = [&](uint32_t slot) { reg.Hdr(int(slot))->seq = reg.Hdr(int(slot))->seq + 2; };
  CHECK(DoRead(r, reg, d, 2000) == ReadResult::Reused);  // both candidates torn -> last good (still < 500 ms)
  CHECK(d.Y(0, 0) == 0x50);
}

TEST_CASE("Reader_LatestSlotNoneOrOutOfRangeUsesOtherSlot") {
  for (uint32_t latest : {0xFFFFFFFFu, 7u}) {
    Region reg;
    reg.Write(2, 1280, 720, 1280, 0x42, 0x80, 4, 1000, false);
    reg.Ctl()->latestSlot = latest;
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
    CHECK(d.Y(0, 0) == 0x42);
  }
}

TEST_CASE("Reader_RegionTooSmallIsStale") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, 1000);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(r.Read(reg.mem.data(), kMappingBytes - 1, 1280, 720, 1000, kFreq, d.buf.data(), d.buf.size(), d.pitch) ==
        ReadResult::Stale);
  CHECK(d.Y(0, 0) == 0xEE);
}

TEST_CASE("Reader_FallbackPicksHighestFrameIdAmongOthers") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x15, 0x80, 5, 1000, false);
  reg.Write(1, 1280, 720, 1280, 0x19, 0x80, 9, 1000, false);
  reg.Write(2, 640, 360, 640, 0x77, 0x80, 12, 1000, false);  // latest, invalid size
  reg.Ctl()->latestSlot = 2;
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x19);
}

TEST_CASE("Reader_DoesNotTouchPitchPadding") {
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x90, 1, 1000);
  FrameReader r;
  Dst d(1280, 720, 1408);
  CHECK(DoRead(r, reg, d, 1000) == ReadResult::Fresh);
  CHECK(d.Y(1279, 10) == 0x50);
  CHECK(d.Y(1280, 10) == 0xEE);
  CHECK(d.Y(1407, 719) == 0xEE);
  CHECK(d.UV(1280, 5) == 0xEE);
  CHECK(d.UV(1407, 359) == 0xEE);
}

TEST_CASE("Dacl_AcceptsOnlyUserSids") {
  CHECK(BuildMappingSddl(L"IU").empty());
  CHECK(BuildMappingSddl(L"BA").empty());
  CHECK(BuildMappingSddl(L"S-1-12-1-1-2-3-4") == L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;S-1-12-1-1-2-3-4)");
  CHECK(BuildMappingSddl(L"S-1-1-0").empty());       // Everyone
  CHECK(BuildMappingSddl(L"S-1-5-11").empty());      // Authenticated Users
  CHECK(BuildMappingSddl(L"S-1-5-32-545").empty());  // BUILTIN\Users
  CHECK(BuildMappingSddl(L"S-1-5-32-544").empty());  // BUILTIN\Administrators
  CHECK(BuildMappingSddl(L"S-1-5-18").empty());      // SYSTEM
  CHECK(BuildMappingSddl(L"S-1-5-21").empty());      // prefix without a trailing dash
  CHECK(BuildMappingSddl(L"S-1-5-21-").empty());     // empty subauthority
  CHECK(BuildMappingSddl(L"S-1-5-").empty());
  CHECK(BuildMappingSddl(L"S-1-5-21)(A;;GA;;;WD").empty());
}

TEST_CASE("Card_TinyFrameAndBadPitch") {
  const uint32_t w = 64, h = 48;
  std::vector<uint8_t> a(size_t(w) * h * 3 / 2, 0xEE);
  DrawPlaceholderCard(a.data(), w, w, h, 7);
  CHECK(a[0] == 0x20);
  CHECK(a[size_t(w) * (h - 1) + (w - 1)] == 0x20);
  std::vector<uint8_t> b(a.size(), 0xEE);
  DrawPlaceholderCard(b.data(), w - 2, w, h, 0);  // pitch < width: no writes
  CHECK(b[0] == 0xEE);
  CHECK(b.back() == 0xEE);
  // narrow frame: square is clamped to width
  std::vector<uint8_t> c(size_t(4) * 400 * 3 / 2, 0xEE);
  DrawPlaceholderCard(c.data(), 4, 4, 400, 0);
  CHECK(c[0] == 0x20);
}

TEST_CASE("Reader_OldSlotsNotReplayedAfterStale") {
  // A dead writer leaves three complete slots behind; none of them may be served once they are older than 0.5 s.
  const int64_t t = 100 * kFreq;
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x10, 0x80, 10, t - 1000 * kMs, false);
  reg.Write(1, 1280, 720, 1280, 0x11, 0x80, 11, t - 900 * kMs, false);
  reg.Write(2, 1280, 720, 1280, 0x12, 0x80, 12, t - 800 * kMs, true);
  FrameReader r;
  Dst d(1280, 720);
  CHECK(DoRead(r, reg, d, t) == ReadResult::Stale);
  CHECK(d.Y(0, 0) == 0xEE);
  for (int i = 1; i <= 10; i++) CHECK(DoRead(r, reg, d, t + i * 33 * kMs) == ReadResult::Stale);
}

TEST_CASE("Reader_FreshSlotAcceptedAfterStale") {
  const int64_t t = 100 * kFreq;
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x10, 0x80, 10, t - 1000 * kMs, false);
  reg.Write(1, 1280, 720, 1280, 0x11, 0x80, 11, t - 900 * kMs, false);
  reg.Write(2, 1280, 720, 1280, 0x12, 0x80, 12, t - 800 * kMs, true);
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, t) == ReadResult::Stale);
  reg.Write(0, 1280, 720, 1280, 0x77, 0x80, 1, t);  // restarted writer, numbering from 1 again
  CHECK(DoRead(r, reg, d, t) == ReadResult::Fresh);
  CHECK(d.Y(0, 0) == 0x77);
}

TEST_CASE("Reader_FutureTimestampRejected") {
  const int64_t t = 100 * kFreq;
  {
    Region reg;
    reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, t + 2000 * kMs);  // 2 s in the future
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, t) == ReadResult::Stale);
    CHECK(d.Y(0, 0) == 0xEE);
  }
  {
    Region reg;
    reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, t + 500 * kMs);  // slightly ahead is tolerated
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, t) == ReadResult::Fresh);
  }
}

TEST_CASE("Reader_ExtremeTimestampsAreNotFresh") {
  const int64_t t = 100 * kFreq;
  const int64_t extremes[] = {INT64_MIN, INT64_MIN + 1, -1, 0, INT64_MAX, INT64_MAX - 1};
  for (int64_t ts : extremes) {
    Region reg;
    reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, ts);
    FrameReader r;
    Dst d(1280, 720);
    CHECK(DoRead(r, reg, d, t) != ReadResult::Fresh);
    CHECK(d.Y(0, 0) == 0xEE);
  }
}

TEST_CASE("Reader_FallbackToleratesClockGoingBackwards") {
  const int64_t t = 100 * kFreq;
  Region reg;
  reg.Write(0, 1280, 720, 1280, 0x50, 0x80, 1, t + 800 * kMs);  // slightly ahead of now: accepted
  FrameReader r;
  Dst d(1280, 720);
  REQUIRE(DoRead(r, reg, d, t) == ReadResult::Fresh);
  // now earlier than the last good timestamp: age clamps to 0, so the frame is reused (no flapping to the card)
  CHECK(DoRead(r, reg, d, t - 100 * kMs) == ReadResult::Reused);
}

TEST_CASE("ActiveStreamSet_FollowsMostRecentActiveStream") {
  ActiveStreamSet s;
  int a, b, c;
  uint32_t w = 0, h = 0;
  CHECK_FALSE(s.Current(w, h));
  s.Start(&a, 1920, 1080);
  REQUIRE(s.Current(w, h));
  CHECK((w == 1920 && h == 1080));
  s.Start(&b, 1280, 720);
  REQUIRE(s.Current(w, h));
  CHECK((w == 1280 && h == 720));
  s.Start(&c, 640, 360);
  s.Stop(&c);  // most recent stopped: fall back to the previous one, not to inactive
  REQUIRE(s.Current(w, h));
  CHECK((w == 1280 && h == 720));
  s.Stop(&a);  // an older one stopping changes nothing
  REQUIRE(s.Current(w, h));
  CHECK((w == 1280 && h == 720));
  s.Stop(&b);
  CHECK_FALSE(s.Current(w, h));
  s.Stop(&b);  // unknown key: no-op
  CHECK_FALSE(s.Current(w, h));
}

TEST_CASE("ActiveStreamSet_RestartMakesStreamMostRecent") {
  ActiveStreamSet s;
  int a, b;
  uint32_t w = 0, h = 0;
  s.Start(&a, 1920, 1080);
  s.Start(&b, 1280, 720);
  s.Start(&a, 1920, 1080);  // a restarted
  REQUIRE(s.Current(w, h));
  CHECK((w == 1920 && h == 1080));
}
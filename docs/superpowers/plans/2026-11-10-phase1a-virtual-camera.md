# Phase 1A — Production Virtual Camera Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Phase 0 spike camera into the production "RaaiCam" virtual camera: tear-free triple-buffered shared memory, timer-paced delivery, safe permissions, admin-only install location, a built-in "no signal" card, consumer state + client-app detection, and a test-pattern source. All of it is testable without the iPhone.

**Architecture:** A C++ Media Foundation media source DLL (`windows/RaaiCam.VirtualCam`), loaded by Windows Frame Server, owns a `Global\` shared-memory region of 3 frame slots, each protected by a sequence lock. A C# library (`windows/RaaiCam.Media`) writes NV12 frames into it through `VirtualCameraSink`. A small CLI (`vcamctl.exe`) registers and creates the camera; an elevated dev-install script puts the DLL in `%ProgramFiles%\RaaiCam`. A dev console (`raaicam-dev`) feeds a moving test pattern so the whole PC side runs without a phone. Phases 1B/1C plug the real phone video in as another frame source.

**Tech Stack:** C++20, Media Foundation, WRL/WIL (FetchContent, header-only), doctest (FetchContent, header-only), CMake 4.4 + NMake (VS 2026 Build Tools at `D:\VS\BuildTools`, SDK at `D:\WindowsKits`); .NET 10 (C#, xUnit).

**Spec:** `docs/superpowers/specs/2026-11-10-raaicam-design.md` (FR-VC-1..4, FR-VC-6, §9.3, §9.5, D17, D18) · Phase 0 lessons: `docs/spikes/phase0-results.md` → "Implications for Phase 1".

## Global Constraints

- Windows 11 only (build ≥ 22000). Camera friendly name: **"RaaiCam"**. Formats exposed: **NV12 1920×1080@30 and 1280×720@30** only.
- `MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_System, MFVirtualCameraAccess_CurrentUser, L"RaaiCam", <CLSID>, nullptr, 0, &cam)`.
- New production CLSID (generate once, record in `windows/RaaiCam.VirtualCam/src/Ids.h`); must differ from the spike's `{7EC2CD0F-93C4-4E06-BF91-63F3B541C1E4}`.
- DLL installed **only** to `%ProgramFiles%\RaaiCam\VirtualCam\` (admin-only-writable); COM registration in HKLM via one elevated step; never register a DLL from a user-writable path.
- Shared memory DACL: `SYSTEM` GA, `LocalService` GA, and **only the allowed user SID** with `GENERIC_READ|GENERIC_WRITE` (never GA, never `IU`). The allowed SID is read from `HKLM\SOFTWARE\RaaiCam\VirtualCam` value `AllowedUserSid` (REG_SZ). If it is missing, the DLL refuses to create the mapping, logs it, and serves the built-in card.
- No work under the loader lock: `DllMain` only stores the module handle / disables thread calls. No `GetUserNameW`, file or registry I/O there.
- DLL logging: ETW-free simple file log at `%ProgramData%\RaaiCam\logs\vcam.log` (created by the installer with Users: no access, LocalService: modify), plus `OutputDebugStringW` lines prefixed exactly `[RaaiCamVCam] `.
- App-side logs: `%LOCALAPPDATA%\RaaiCam\logs\` (spec §9.5).
- No outbound internet at runtime. Build-time FetchContent is allowed (Phase 0 ruling).
- C: has ~2 GB free: keep build outputs under `D:\RaaiCam`; install nothing large.
- Build env: `$env:WindowsSdkDir='D:\WindowsKits\'; $env:WindowsSDKVersion='10.0.26100.0\'` then `D:\VS\BuildTools\Common7\Tools\Launch-VsDevShell.ps1 -Arch amd64 -HostArch amd64 -SkipAutomaticLocation`; CMake generator `NMake Makefiles`.
- Spike code under `spikes/` stays untouched (reference only). Production code lives under `windows/` and `protocol/`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; repo-local `user.email` is the GitHub noreply address (already configured).

## Review Focus

1. **Writer dies mid-write**: a slot's sequence stays odd forever. The reader must treat it as unusable, fall back to the newest complete slot, and fall back to the "no signal" card once nothing complete is newer than 500 ms. Test: Task 2 `Reader_IgnoresOddSequenceSlot`, `Reader_StaleAfter500ms`.
2. **Resolution switch while streaming** (the consumer picks 720p while the writer still writes 1080p): the reader must reject size-mismatched slots, not crop or scale garbage, and the writer must switch within one frame of seeing the new request. Tests: Task 2 `Reader_RejectsSizeMismatch`; Task 1 `Writer_FollowsRequestedSize`.
3. **Frame Server restart / mapping recreated** while the app keeps running: the writer must notice (consumer generation changed, or the mapping vanished) and reopen without an app restart. Test: Task 1 `Writer_ReopensWhenGenerationChanges`; Task 6 manual step "restart FrameServer service".
4. **App not running at all**: a meeting app must still get valid 30 fps video (the built-in card) and never black or a hang. Test: Task 6 probe check A.
5. **Another Windows user / non-allowed SID**: must not open the mapping. Test: Task 2 `Dacl_ContainsOnlyAllowedPrincipals` (asserts the SDDL built from a given SID); Task 6 probe check F (opening with the wrong expected name/permissions fails cleanly).

---

## Shared-memory layout v2 (single source of truth: `protocol/SHARED_FRAME.md`)

Names: mapping `Global\RaaiCam.VCam.Frames.v2`. No events: the reader is timer-paced and the writer polls the header.

All little-endian, `#pragma pack(1)`, offsets fixed:

```
ControlHeader (256 bytes, offset 0)
  0   u32 magic            = 0x32464352 ('RCF2')
  4   u32 version          = 2
  8   u32 headerBytes      = 256
  12  u32 slotCount        = 3
  16  u32 slotStride       = 64 + 3110400   (SlotHeader + max NV12 1920x1080)
  20  u32 maxWidth         = 1920
  24  u32 maxHeight        = 1080
  28  u32 _pad0
  -- written by the reader (DLL) --
  32  u64 consumerGeneration   incremented each time the mapping is (re)created; never 0 once valid
  40  u32 consumerActive       1 while ≥1 stream is started
  44  u32 requestedWidth       size the active stream negotiated (0 when inactive)
  48  u32 requestedHeight
  52  u32 _pad1
  56  i64 readerHeartbeatQpc   updated every delivery tick
  -- written by the writer (app) --
  64  u32 writerPid
  68  u32 latestSlot           index of the most recently completed slot (0..2), 0xFFFFFFFF = none
  72  i64 writerHeartbeatQpc   updated every write
  80  .. 255 reserved (zero)

Slot i at offset 256 + i*slotStride:
  SlotHeader (64 bytes)
  0   u64 seq          even = stable, odd = being written (seqlock)
  8   u32 width
  12  u32 height
  16  u32 stride       bytes per Y row, >= width
  20  u32 format       = 0x3231564E ('NV12')
  24  i64 timestampQpc capture/compose time
  32  u64 frameId      monotonically increasing per writer
  40  .. 63 reserved
  then stride*height*3/2 bytes of NV12
```

Writer protocol: pick a slot ≠ `latestSlot` (round-robin among the other two) → `seq++` (odd) → full barrier → write the fields + pixels → full barrier → `seq++` (even) → `latestSlot = i` → `writerHeartbeatQpc = now`.
Reader protocol: read `latestSlot` → `s1 = seq` (must be even) → copy the header fields **once** into locals → validate (format, `width/height == requested`, `stride >= width`, `stride*height*3/2 <= slotStride-64`) → copy the pixels → barrier → `s2 = seq`; accept only if `s1 == s2`. Otherwise retry once with the other complete slots; else reuse the last good frame if it is ≤ 500 ms old, else the card.

---

## File Structure

```
protocol/SHARED_FRAME.md                         layout above (normative)
windows/
├─ build.ps1                                     builds C++ (CMake/NMake) + .NET solution; one entry point
├─ RaaiCam.sln
├─ RaaiCam.VirtualCam/                           C++
│  ├─ CMakeLists.txt                             targets: RaaiCamVCam (DLL), vcamctl (exe), vcam_tests (exe), vcam-probe (exe)
│  ├─ src/Ids.h                                  CLSID + names
│  ├─ src/SharedFrameV2.h                        layout structs + constants (static_asserts on offsets)
│  ├─ src/FrameReader.{h,cpp}                    pure logic over a byte span: validate + seqlock copy + staleness (no Win32 objects)
│  ├─ src/SharedRegion.{h,cpp}                   CreateFileMapping with DACL built from AllowedUserSid; generation bump
│  ├─ src/Security.{h,cpp}                       BuildMappingSddl(sidString) + ReadAllowedUserSid()
│  ├─ src/PlaceholderCard.{h,cpp}                procedural NV12 "no signal" card (dark bg, centered RaaiCam mark, slow pulse)
│  ├─ src/DeliveryClock.{h,cpp}                  threadpool timer @ 30 fps; delivers queued requests
│  ├─ src/MediaSource.{h,cpp}, MediaStream.{h,cpp}, Activator.{h,cpp}, dllmain.cpp, RaaiCamVCam.def
│  │                                             ported from the spike with the Phase 0 fixes (per-stream state, lock discipline)
│  ├─ src/Log.{h,cpp}                            lazy-initialized file log (first use, never in DllMain)
│  ├─ vcamctl/main.cpp                           create | remove | status | register-dll | unregister-dll
│  ├─ tests/vcam_tests.cpp                       doctest: FrameReader, Security, PlaceholderCard
│  └─ probe/main.cpp                             ported spike probe + tearing check + JSON summary
├─ RaaiCam.Media/                                C# class library (net10.0-windows)
│  ├─ SharedFrameLayout.cs                       offsets/constants mirroring SharedFrameV2.h
│  ├─ VirtualCameraSink.cs                       opens mapping, writes frames (seqlock), exposes ConsumerState
│  ├─ ITestPatternSource / TestPatternSource.cs  moving NV12 test pattern with frameId encoded top+bottom rows
│  └─ CameraUsageMonitor.cs                      FR-VC-6: which exe is using a camera (CapabilityAccessManager registry)
├─ RaaiCam.Media.Tests/                          xUnit
├─ tools/raaicam-dev/                            C# console: test pattern → sink, prints consumer state + client app
└─ install/
   ├─ dev-install.ps1                            elevated: copy DLL to %ProgramFiles%\RaaiCam\VirtualCam, ACLs, regsvr32, AllowedUserSid, ProgramData log dir
   └─ dev-uninstall.ps1                          elevated: reverse of the above
docs/checklists/phase-1a.md                      owner checklist
```

---

### Task 1: Shared-frame contract + C# writer

**Files:**
- Create: `protocol/SHARED_FRAME.md`, `windows/RaaiCam.sln`, `windows/RaaiCam.Media/RaaiCam.Media.csproj`, `windows/RaaiCam.Media/SharedFrameLayout.cs`, `windows/RaaiCam.Media/VirtualCameraSink.cs`
- Test: `windows/RaaiCam.Media.Tests/RaaiCam.Media.Tests.csproj`, `windows/RaaiCam.Media.Tests/VirtualCameraSinkTests.cs`

**Interfaces:**
- Produces:
  - `static class SharedFrameLayout` with `const` values for every offset/constant in the layout section (names: `Magic`, `Version`, `HeaderBytes`, `SlotCount`, `SlotHeaderBytes`, `MaxFrameBytes`, `SlotStride`, `MappingBytes`, `OffConsumerGeneration`, `OffConsumerActive`, `OffRequestedWidth`, `OffRequestedHeight`, `OffReaderHeartbeat`, `OffWriterPid`, `OffLatestSlot`, `OffWriterHeartbeat`, `SlotOff(int i)`, `SlotOffSeq`, `SlotOffWidth`, `SlotOffHeight`, `SlotOffStride`, `SlotOffFormat`, `SlotOffTimestamp`, `SlotOffFrameId`, `FormatNv12`, `MappingName = @"Global\RaaiCam.VCam.Frames.v2"`).
  - `public sealed record ConsumerState(bool Active, int RequestedWidth, int RequestedHeight, ulong Generation)`.
  - `public sealed class VirtualCameraSink : IDisposable`:
    - `VirtualCameraSink(string mappingName = SharedFrameLayout.MappingName, Func<long>? qpcNow = null)`
    - `bool TryOpen()`: opens the existing mapping (never creates it); false if absent or the magic/version is wrong.
    - `ConsumerState? ReadConsumerState()`: null when not open.
    - `bool WriteFrame(ReadOnlySpan<byte> nv12, int width, int height, int stride, long timestampQpc)`: false if not open, or the size ≠ requested size, or the buffer is too small. Implements the writer protocol.
    - `event Action<ConsumerState>? ConsumerChanged` is NOT included (YAGNI); callers poll.
    - Reopen rule: every `WriteFrame`/`ReadConsumerState` compares `consumerGeneration` with the value seen at open; if it changed or reads 0, dispose the view and `TryOpen()` again.
- Test helper (in the test project only): `TestRegion.Create(string localName)` creates a `Local\` mapping of `MappingBytes` and initializes the ControlHeader like the DLL would (magic, version, sizes, generation=1, `latestSlot=0xFFFFFFFF`), with `SetRequested(w,h,active)` and `BumpGeneration()`.

- [ ] **Step 1: Write `protocol/SHARED_FRAME.md`** — copy the layout section of this plan verbatim plus a "Versioning" note: any layout change bumps `magic`/`version` and the mapping name suffix.

- [ ] **Step 2: Write failing tests** in `VirtualCameraSinkTests.cs` (use a unique `Local\RaaiCamTest.<guid>` name per test):

```csharp
[Fact] public void Layout_OffsetsMatchSpec()
{
    Assert.Equal(256, SharedFrameLayout.HeaderBytes);
    Assert.Equal(64 + 1920 * 1080 * 3 / 2, SharedFrameLayout.SlotStride);
    Assert.Equal(256 + 3 * SharedFrameLayout.SlotStride, SharedFrameLayout.MappingBytes);
    Assert.Equal(32, SharedFrameLayout.OffConsumerGeneration);
    Assert.Equal(68, SharedFrameLayout.OffLatestSlot);
    Assert.Equal(256 + SharedFrameLayout.SlotStride, SharedFrameLayout.SlotOff(1));
}
[Fact] public void TryOpen_FalseWhenMappingMissing() { /* new sink on unused name → TryOpen()==false */ }
[Fact] public void TryOpen_FalseWhenMagicWrong() { /* TestRegion with magic 0 → false */ }
[Fact] public void Write_RejectedWhenNotRequestedSize() { /* requested 1280x720 active; write 1920x1080 → false; latestSlot stays 0xFFFFFFFF */ }
[Fact] public void Write_CompletesSlotWithEvenSeqAndPublishesLatest()
{
    // requested 1280x720; write a frame filled with 0x42
    // assert latestSlot in {0,1,2}; that slot: seq even and > 0, width 1280, height 720, stride 1280, format NV12,
    // frameId == 1, first and last pixel byte == 0x42; writerPid == Environment.ProcessId; writerHeartbeat == injected qpc
}
[Fact] public void Write_NeverOverwritesLatestSlot() { /* write 6 frames; record latestSlot before each write; assert the new latestSlot != previous every time; frameIds 1..6 */ }
[Fact] public void Writer_FollowsRequestedSize() { /* requested 1920x1080 → write 1080p ok; SetRequested(1280,720) → 1080p write false, 720p write true */ }
[Fact] public void Writer_ReopensWhenGenerationChanges() { /* open; BumpGeneration(); next WriteFrame/ReadConsumerState returns state with new Generation and write succeeds */ }
[Fact] public void ReadConsumerState_ReflectsReaderFields() { /* SetRequested(1280,720,active:true) → (true,1280,720,1) */ }
```

- [ ] **Step 3: Run and confirm failure**

Run: `dotnet test windows/RaaiCam.Media.Tests`
Expected: FAIL — compile errors (`SharedFrameLayout`, `VirtualCameraSink` not defined).

- [ ] **Step 4: Implement** `SharedFrameLayout` and `VirtualCameraSink` with the signatures above. Use `MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite)` + one `MemoryMappedViewAccessor` over the whole mapping; pointer access via `SafeMemoryMappedViewHandle.AcquirePointer` and `Volatile.Read/Write` + `Interlocked.MemoryBarrier()` for the seqlock steps. Copy pixels with `Buffer.MemoryCopy` / `Span.CopyTo`.

- [ ] **Step 5: Run tests and confirm they pass**

Run: `dotnet test windows/RaaiCam.Media.Tests`
Expected: PASS (9 tests), no warnings.

- [ ] **Step 6: Commit**

```bash
git add protocol/SHARED_FRAME.md windows/RaaiCam.sln windows/RaaiCam.Media windows/RaaiCam.Media.Tests
git commit -m "feat(media): shared-frame v2 contract and VirtualCameraSink writer"
```

---

### Task 2: C++ reader logic, security and placeholder card (unit-tested, no Frame Server)

**Files:**
- Create: `windows/RaaiCam.VirtualCam/CMakeLists.txt`, `src/SharedFrameV2.h`, `src/FrameReader.{h,cpp}`, `src/Security.{h,cpp}`, `src/PlaceholderCard.{h,cpp}`, `tests/vcam_tests.cpp`, `windows/build.ps1`

**Interfaces:**
- Consumes: the layout (Task 1 / `protocol/SHARED_FRAME.md`).
- Produces:
  - `SharedFrameV2.h`: `struct ControlHeader`, `struct SlotHeader` (packed, `static_assert` on `sizeof` and on `offsetof` for every field listed in the layout), constants `kMagic`, `kVersion`, `kSlotCount`, `kSlotHeaderBytes`, `kMaxFrameBytes`, `kSlotStride`, `kMappingBytes`, `kMappingName = L"Global\\RaaiCam.VCam.Frames.v2"`.
  - `enum class ReadResult { Fresh, Reused, Stale }`
  - `class FrameReader { public: ReadResult Read(const uint8_t* region, size_t regionBytes, uint32_t width, uint32_t height, int64_t nowQpc, int64_t qpcFreq, uint8_t* dst, size_t dstBytes, uint32_t dstPitch); private: last good frameId, last good QPC, has-last flag; }`. `dst` is NV12 with `dstPitch` (≥ width), Y plane then UV plane at `dst + dstPitch*height`. **Reused** = the last good frame is still in `dst`, unchanged, ≤ 500 ms old; **Stale** = the caller must draw the card.
  - `std::wstring BuildMappingSddl(const std::wstring& allowedUserSid)` → exactly `D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;<sid>)`; empty string when `allowedUserSid` isn't a syntactically valid SID (`ConvertStringSidToSidW` fails).
  - `std::optional<std::wstring> ReadAllowedUserSid()` (reads `HKLM\SOFTWARE\RaaiCam\VirtualCam\AllowedUserSid`).
  - `void DrawPlaceholderCard(uint8_t* dst, uint32_t pitch, uint32_t width, uint32_t height, uint64_t tick)` — background Y=0x20 UV=0x80; centered rounded square (side = height/5) Y=0xB0 whose brightness pulses ±0x10 over a 2 s period of `tick` at 30 fps; no text.

- [ ] **Step 1: CMake + build script.** `CMakeLists.txt` adapted from `spikes/vcam/CMakeLists.txt` (static CRT, `/W4 /WX /permissive- /utf-8`, WIL + doctest via FetchContent, outputs to `windows/RaaiCam.VirtualCam/build/bin`). In this task, only the `vcam_tests` target exists (`src/FrameReader.cpp src/Security.cpp src/PlaceholderCard.cpp tests/vcam_tests.cpp`). `windows/build.ps1` = the Global Constraints env setup + `cmake -S windows/RaaiCam.VirtualCam -B windows/RaaiCam.VirtualCam/build -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release` + `cmake --build` + `dotnet build windows/RaaiCam.sln -c Release`; with `-Test` it also runs `vcam_tests.exe` and `dotnet test`.

- [ ] **Step 2: Write failing doctest cases** in `tests/vcam_tests.cpp` (build regions in a `std::vector<uint8_t>(kMappingBytes)` with a small helper that writes slots the way the Task 1 writer does):

```cpp
TEST_CASE("Reader_FreshFrameCopied")          // slot with even seq, 1280x720, Y=0x50 → Fresh, dst Y==0x50, UV copied
TEST_CASE("Reader_IgnoresOddSequenceSlot")    // latest slot seq odd, older slot complete+fresh → Fresh from older slot
TEST_CASE("Reader_RejectsSizeMismatch")       // slot 1920x1080, requested 1280x720 → not Fresh (Stale when no last good)
TEST_CASE("Reader_RejectsOversizedStride")    // stride*h*3/2 > kSlotStride-64 → not Fresh
TEST_CASE("Reader_RejectsBadFormatOrMagic")   // format != NV12 or control magic wrong → Stale
TEST_CASE("Reader_ReusedWithin500ms")         // Fresh at t, nothing new at t+400ms → Reused
TEST_CASE("Reader_StaleAfter500ms")           // Fresh at t, nothing new at t+600ms → Stale
TEST_CASE("Reader_SameFrameIdIsReused")       // slot unchanged between reads → second call Reused, not Fresh
TEST_CASE("Dacl_ContainsOnlyAllowedPrincipals")
    // BuildMappingSddl(L"S-1-5-21-1-2-3-1001") == L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GRGW;;;S-1-5-21-1-2-3-1001)"
    // BuildMappingSddl(L"not-a-sid").empty()
TEST_CASE("Card_FillsWholeFrameAndPulses")    // 1280x720: corner Y==0x20, center Y in [0xA0,0xC0], UV all 0x80; tick 0 vs tick 30 center differs
```

- [ ] **Step 3: Run and confirm failure**

Run: `powershell -ExecutionPolicy Bypass -File windows/build.ps1 -Test`
Expected: FAIL (compile errors: `FrameReader`, `BuildMappingSddl`, `DrawPlaceholderCard` undefined).

- [ ] **Step 4: Implement** `SharedFrameV2.h`, `FrameReader`, `Security`, `PlaceholderCard` per the interfaces and the reader protocol. Header fields are read **once** into locals through `volatile` reads before validation. Use `std::atomic_thread_fence(std::memory_order_acquire)` around the seqlock reads.

- [ ] **Step 5: Run and confirm pass**

Run: `powershell -ExecutionPolicy Bypass -File windows/build.ps1 -Test`
Expected: `vcam_tests` reports 10 test cases passed, 0 failed; the .NET tests still pass; zero compiler warnings (`/WX`).

- [ ] **Step 6: Commit**

```bash
git add windows/build.ps1 windows/RaaiCam.VirtualCam
git commit -m "feat(vcam): seqlock frame reader, mapping DACL and placeholder card"
```

---

### Task 3: Production media source DLL

**Files:**
- Create: `windows/RaaiCam.VirtualCam/src/{Ids.h, SharedRegion.h/.cpp, DeliveryClock.h/.cpp, Log.h/.cpp, MediaSource.h/.cpp, MediaStream.h/.cpp, Activator.h/.cpp, dllmain.cpp, RaaiCamVCam.def}` plus the needed spike helpers (`Tools`, `MFTools`, `EnumNames`, `WinTrace`, `Undocumented.h`, `framework.h`, `pch.h`) copied from `spikes/vcam/src/` with their MIT attribution headers kept
- Modify: `windows/RaaiCam.VirtualCam/CMakeLists.txt` (add the `RaaiCamVCam` SHARED target), `windows/THIRD_PARTY_NOTICES.md` (create: VCamSample MIT text + WIL + doctest)

**Interfaces:**
- Consumes: `FrameReader`, `BuildMappingSddl`, `ReadAllowedUserSid`, `DrawPlaceholderCard` (Task 2); layout constants.
- Produces:
  - `Ids.h`: `constexpr GUID CLSID_RaaiCamVCam` (new GUID) and `kFriendlyName = L"RaaiCam"`.
  - `class SharedRegion { public: static SharedRegion& Instance(); uint8_t* TryAcquire(); /* creates the mapping on first success using the SDDL; bumps consumerGeneration on every (re)creation; returns nullptr while AllowedUserSid is missing (retries at most once per 2 s) */ void PublishConsumer(bool active, uint32_t w, uint32_t h); void Heartbeat(int64_t qpc); }` — `PublishConsumer` is called again whenever the mapping first becomes available while streams are active (fixes the spike's "late mapping" bug).
  - `class DeliveryClock` — one threadpool timer (`CreateThreadpoolTimer`) at 33.333 ms per started stream; each tick fulfils at most one pending `RequestSample`; pending count is incremented under the stream lock in `RequestSample` and drained on the tick; Stop/Shutdown cancel the timer with `WaitForThreadpoolTimerCallbacks(timer, TRUE)` **outside** the stream lock.
  - Per-stream state (no process-wide FrameSource singleton): each `MediaStream` owns its `FrameReader`, card tick and staging buffer.
  - On any failure to produce a frame (no region, read error), the stream fills the placeholder card; it never drops a request silently. If sample allocation itself fails, queue `MEError` with the HRESULT.

- [ ] **Step 1: Port the spike media source** into the new files with: new CLSID/name, formats exactly NV12 1920×1080@30 + 1280×720@30, the spike's race fix kept (lock in Start/Stop/Shutdown), sleep-based pacing **removed** in favor of `DeliveryClock`, `DllMain` reduced to `DisableThreadLibraryCalls` + module handle, `Log` lazily initialized on first use.
- [ ] **Step 2: Build**

Run: `powershell -ExecutionPolicy Bypass -File windows/build.ps1 -Test`
Expected: `RaaiCamVCam.dll` built, `/W4 /WX` clean, the Task 2 tests still pass.

- [ ] **Step 3: Self-check with `dumpbin /exports`** that only `DllGetClassObject`, `DllCanUnloadNow`, `DllRegisterServer`, `DllUnregisterServer` are exported.

Run: `dumpbin /exports windows\RaaiCam.VirtualCam\build\bin\RaaiCamVCam.dll` (inside the dev shell)
Expected: exactly those 4 names.

- [ ] **Step 4: Commit**

```bash
git add windows/RaaiCam.VirtualCam windows/THIRD_PARTY_NOTICES.md
git commit -m "feat(vcam): production media source with timer-paced delivery and per-stream state"
```

(End-to-end behavior is verified in Task 6, after installation exists.)

---

### Task 4: `vcamctl` + elevated dev install / uninstall

**Files:**
- Create: `windows/RaaiCam.VirtualCam/vcamctl/main.cpp` (+ CMake target), `windows/install/dev-install.ps1`, `windows/install/dev-uninstall.ps1`

**Interfaces:**
- Consumes: `CLSID_RaaiCamVCam`, `kFriendlyName`.
- Produces:
  - `vcamctl create` → `MFCreateVirtualCamera(... Lifetime_System, Access_CurrentUser, L"RaaiCam", CLSID ...)` + `Start(nullptr)`; prints `CREATED` (exit 0) or `ALREADY_EXISTS` (exit 0) or `ERROR 0x%08X` (exit 2).
  - `vcamctl remove` → opens the existing camera by symbolic link via `MFCreateVirtualCamera` + `Remove()`; prints `REMOVED` / `NOT_FOUND` (both exit 0).
  - `vcamctl status` → prints one line of JSON: `{"registered":bool,"dllPath":"...","dllPathAdminOnly":bool,"cameraPresent":bool,"allowedUserSid":"..."|null}` (`cameraPresent` via `MFEnumDeviceSources` name match "RaaiCam"; `dllPathAdminOnly` = the DLL folder ACL grants write only to Administrators/SYSTEM/TrustedInstaller).
  - `dev-install.ps1` (must be run elevated; it self-elevates with `Start-Process -Verb RunAs` if not):
    1. `param([string]$UserSid)`, default = the SID of the user owning the `explorer.exe` process in the current session (so an over-the-shoulder admin elevation still targets the logged-in user).
    2. Copies `RaaiCamVCam.dll` to `%ProgramFiles%\RaaiCam\VirtualCam\` (inherits Program Files ACLs). If the destination is locked (Frame Server holds it), it retries for up to 60 s, then offers `Restart-Service FrameServer`.
    3. `regsvr32 /s` on the **installed** path.
    4. Writes `HKLM\SOFTWARE\RaaiCam\VirtualCam\AllowedUserSid` = `$UserSid`.
    5. Creates `%ProgramData%\RaaiCam\logs` with an ACL of SYSTEM full, LocalService modify, Administrators full, no Users access.
    6. Prints a summary; then the non-elevated caller runs `vcamctl create`.
  - `dev-uninstall.ps1`: `vcamctl remove` (non-elevated part first), then elevated `regsvr32 /u /s`, deletes the HKLM key, deletes `%ProgramFiles%\RaaiCam\VirtualCam` (with the same lock retry).

- [ ] **Step 1: Implement `vcamctl`** with the commands above; add the CMake target.
- [ ] **Step 2: Implement the two scripts.**
- [ ] **Step 3: Static check of the scripts**

Run: `powershell -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('windows/install/dev-install.ps1',[ref]$null,[ref]$e); $e.Count"` (same for uninstall)
Expected: `0` for both.

- [ ] **Step 4: Run install (owner approves one UAC prompt), then status**

Run: `powershell -ExecutionPolicy Bypass -File windows/install/dev-install.ps1; windows\RaaiCam.VirtualCam\build\bin\vcamctl.exe create; windows\RaaiCam.VirtualCam\build\bin\vcamctl.exe status`
Expected: `CREATED`; status JSON has `"registered":true`, a `dllPath` under `C:\Program Files\RaaiCam\VirtualCam\`, `"dllPathAdminOnly":true`, `"cameraPresent":true`, and the owner's SID.

- [ ] **Step 5: Commit**

```bash
git add windows/RaaiCam.VirtualCam/vcamctl windows/RaaiCam.VirtualCam/CMakeLists.txt windows/install
git commit -m "feat(vcam): vcamctl and admin-only dev install/uninstall"
```

---

### Task 5: Test-pattern source, camera-usage monitor, `raaicam-dev` console

**Files:**
- Create: `windows/RaaiCam.Media/TestPatternSource.cs`, `windows/RaaiCam.Media/CameraUsageMonitor.cs`, `windows/tools/raaicam-dev/raaicam-dev.csproj`, `windows/tools/raaicam-dev/Program.cs`
- Test: `windows/RaaiCam.Media.Tests/TestPatternSourceTests.cs`, `windows/RaaiCam.Media.Tests/CameraUsageMonitorTests.cs`

**Interfaces:**
- Consumes: `VirtualCameraSink`, `ConsumerState` (Task 1).
- Produces:
  - `public sealed class TestPatternSource { public int Render(Span<byte> nv12, int width, int height, int stride, ulong frameId); }` — 8 SMPTE-style bars (same BT.601 values as the spike writer) in the top 2/3; a white block moving 8 px/frame in the bottom 1/3; **frameId encoded** as 64 binary bits in the first and last Y rows (bit = 16-px-wide run, 0x10 for 0 / 0xEB for 1), so a probe can detect tearing (top id ≠ bottom id). Returns bytes written.
  - `public sealed record CameraClient(string ExePath, DateTime StartedUtc)`
  - `public sealed class CameraUsageMonitor { public CameraUsageMonitor(Func<IEnumerable<(string keyName, long lastUsedStart, long lastUsedStop)>> reader); public CameraClient? CurrentClient(); }` — a client is "current" when `LastUsedTimeStop == 0` (or `< LastUsedTimeStart`). Key names under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged` use `#` instead of `\` (convert back); packaged apps come from the sibling keys (key name = package family name). Most recent start wins. `static CameraUsageMonitor CreateDefault()` wires the real registry reader. FILETIME values.
  - `raaicam-dev` console: loop at 30 fps → `sink.TryOpen()` until it succeeds → read `ConsumerState` → if active, render the test pattern at the requested size and `WriteFrame`; once per second print `consumer=active 1280x720 gen=3 client=C:\...\Zoom.exe fps=30.0 written=…`. Ctrl+C exits cleanly.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Pattern_EncodesFrameIdTopAndBottom() { /* Render frameId 0xA5A5_0000_1234_5678 at 1280x720; decode first and last Y rows → same id */ }
[Fact] public void Pattern_BarsUseBt601Values() { /* sample center of bar 0 (Y=0xB4) and bar 7 (Y=0x10) in the middle row */ }
[Fact] public void Pattern_BlockMoves8PxPerFrame() { /* white block x at frame 1 vs 2 differs by 8 */ }
[Fact] public void Usage_ActiveWhenStopIsZero() { /* entries: chrome start=100 stop=0, zoom start=50 stop=60 → chrome */ }
[Fact] public void Usage_MostRecentActiveWins() { /* two active, starts 100 and 200 → the 200 one */ }
[Fact] public void Usage_DecodesHashPath() { /* "C:#Program Files#Zoom#bin#Zoom.exe" → @"C:\Program Files\Zoom\bin\Zoom.exe" */ }
[Fact] public void Usage_NullWhenNoneActive() { }
```

- [ ] **Step 2: Run and confirm failure** — `dotnet test windows/RaaiCam.Media.Tests` → compile errors.
- [ ] **Step 3: Implement** `TestPatternSource`, `CameraUsageMonitor`, and the `raaicam-dev` console.
- [ ] **Step 4: Run and confirm pass** — `dotnet test windows/RaaiCam.Media.Tests` → 16 tests pass (9 + 7).
- [ ] **Step 5: Commit**

```bash
git add windows/RaaiCam.Media windows/RaaiCam.Media.Tests windows/tools/raaicam-dev windows/RaaiCam.sln
git commit -m "feat(media): test pattern source, camera usage monitor, raaicam-dev console"
```

---

### Task 6: Probe, automated end-to-end checks, owner checklist

**Files:**
- Create: `windows/RaaiCam.VirtualCam/probe/main.cpp` (+ CMake target), `windows/tools/run-vcam-checks.ps1`, `docs/checklists/phase-1a.md`
- Modify: `.gitignore` (add `windows/out/`)

**Interfaces:**
- Consumes: the installed camera (Task 4), `raaicam-dev` (Task 5), the frame-id encoding (Task 5).
- Produces: `vcam-probe.exe --name RaaiCam --width W --height H --frames N --json out.json` — prints and writes `{"opened":bool,"fps":double,"cardFrames":int,"liveFrames":int,"tornFrames":int,"distinctFrameIds":int,"error":"..."|null}`. **Card frame** = center Y in the card range and corner Y == 0x20; **live** = frame-id decodes in the top row; **torn** = top id ≠ bottom id.

- [ ] **Step 1: Implement the probe** (port the spike probe's MF capture code; add the classification above).
- [ ] **Step 2: Implement `run-vcam-checks.ps1`**, running these checks and printing PASS/FAIL per check, with the full JSON saved to `windows/out/checks/` (gitignored):
  - **A** No app running → 90 frames at 1280×720: `fps` 28–32, `cardFrames == 90`.
  - **B** `raaicam-dev` running → 300 frames at 1920×1080: `liveFrames ≥ 290`, `tornFrames == 0`, `distinctFrameIds ≥ 280`, fps 28–32.
  - **C** Same at 1280×720 (resolution switch is handled by the consumer reopening at a new size).
  - **D** Kill `raaicam-dev` mid-capture → the card appears within 700 ms (from the probe's per-frame log) and the stream keeps 28–32 fps.
  - **E** 10× start the probe and kill it mid-stream → afterwards a normal probe run still passes A, and `Get-WinEvent` shows no new Application Error (Id 1000) for `svchost.exe_FrameServer` in the last 5 minutes.
  - **F** Restart the `FrameServer` service (this step needs elevation, so it's skipped with a SKIP line when not elevated) while `raaicam-dev` runs → within 5 s `raaicam-dev` reports a new `gen` and the probe sees live frames again.
- [ ] **Step 3: Run the checks**

Run: `powershell -ExecutionPolicy Bypass -File windows/tools/run-vcam-checks.ps1`
Expected: A–E PASS (F PASS when run elevated, otherwise SKIP). Fix any failure before continuing.

- [ ] **Step 4: Write `docs/checklists/phase-1a.md`** with owner steps:
  1. start `raaicam-dev`;
  2. in Zoom, Chrome (Meet preview), Edge and Discord, pick **"RaaiCam"** → moving bars, no tearing visible, and `raaicam-dev` prints the right `client=` exe;
  3. close `raaicam-dev` → apps show the dark card (not black, no freeze);
  4. two apps at once → note what the second shows;
  5. lock and unlock Windows → video continues;
  6. reboot → "RaaiCam" still listed (System lifetime).
  Each item has a pass/fail column.
- [ ] **Step 5: Commit**

```bash
git add .gitignore windows/RaaiCam.VirtualCam/probe windows/RaaiCam.VirtualCam/CMakeLists.txt windows/tools/run-vcam-checks.ps1 docs/checklists/phase-1a.md
git commit -m "test(vcam): probe with tearing detection, automated e2e checks, owner checklist"
```

- [ ] **Step 6: Owner gate** — the owner runs `docs/checklists/phase-1a.md`; results recorded in the same file and committed.

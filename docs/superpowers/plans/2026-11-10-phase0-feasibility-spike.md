# Phase 0 — Feasibility Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the four risky foundations of RaaiCam work on the owner's real hardware before Phase 1: (a) a Windows 11 MF virtual camera fed from a separate process shows up and animates in Zoom, Meet (Chrome/Edge) and Discord; (b) a free GitHub Actions macOS runner builds an unsigned iOS .ipa from code written on Windows; (c) the .ipa installs and runs on the iPhone via SideStore with a free Apple ID; (d) a C# usbmux client reaches a TCP port on the iPhone over USB and receives a camera frame.

**Architecture:** All code lives under `spikes/` and is throwaway (it's not imported by Phase 1). There are four independent probes plus a results report that decides two open spec questions: the virtual-camera feed mechanism, and whether the DirectShow fallback (FR-VC-5) is needed. The virtual camera is a C++ DLL loaded by Windows Frame Server. It reads frames from a named shared-memory buffer written by a small C# "frame writer" process, which is exactly the boundary Phase 1 depends on.

**Tech Stack:** C++20 / Media Foundation / WRL (MSVC Build Tools 2022, CMake) · .NET 10 (C#, xUnit) · Swift 5.10+/SwiftUI/AVFoundation/Network.framework · XcodeGen · GitHub Actions (`macos-15`) · SideStore · Apple Mobile Device Service.

**Spec:** `docs/superpowers/specs/2026-11-10-raaicam-design.md` (§3 D1–D6, D15; §9.3 virtual camera; §9.4 protocol port; §9.6 build & distribution; §13 Phase 0 row; §14 risks)

## Global Constraints

- Windows 11 only; `MFCreateVirtualCamera` requires build ≥ 22000 (owner PC: 10.0.26200).
- Virtual camera registered **per-user** (`MFVirtualCameraAccess_CurrentUser`), no admin needed at runtime. Spike name: **"RaaiCam Test"**.
- Virtual camera formats: NV12 1920×1080@30 and 1280×720@30.
- iPhone app listens on TCP port **47820**.
- usbmux daemon on Windows: `127.0.0.1:27015` (provided by Apple Devices app / Apple Mobile Device Service).
- iOS minimum deployment target **17.0**; bundle id `com.raaicam.spike`; camera usage string required.
- $0: no paid services; GitHub repo is **public**; **no push to GitHub without the owner's explicit OK in chat**.
- No outbound internet traffic from spike apps.
- All spike code under `spikes/`; nothing in `windows/` or `ios/` is created in this phase.
- Third-party code (e.g. smourier/VCamSample, MIT) keeps its license header and is listed in `spikes/THIRD_PARTY_NOTICES.md`.

## Review Focus

1. **Frame writer not running / stopped mid-stream**: the virtual camera must keep delivering frames (a built-in grey "no signal" NV12 pattern) and never block or crash the meeting app. Test: Task 2 Step "writer-absent".
2. **Shared memory across sessions**: Frame Server runs as LocalService in session 0 while the writer runs in the user session; the mapping name and DACL must work for both. Test: Task 2 writer opens the mapping created by the DLL; failure is logged with the Win32 error.
3. **Two apps open the camera at once** (e.g. Camera app + Chrome): the second must get frames or a clean "in use" error, not a hang. Test: Task 2 manual checklist item.
4. **usbmux with no phone, or with the phone app closed**: `Connect` returns result 3 (refused). The client must print a clear message and exit with code 2, not throw. Test: Task 4 unit tests for result parsing + manual check.
5. **iOS app backgrounded or screen locked**: the listener stops. The spike app sets `isIdleTimerDisabled = true`; the checklist records what happens after a manual lock, as input for Phase 1.

---

## File Structure

```
spikes/
├─ THIRD_PARTY_NOTICES.md
├─ vcam/                         (C++ — virtual camera DLL + registrar)
│  ├─ CMakeLists.txt
│  ├─ src/dllmain.cpp            DLL exports, class factory, COM registration (HKCU)
│  ├─ src/Activator.{h,cpp}      IMFActivate returning MediaSource
│  ├─ src/MediaSource.{h,cpp}    IMFMediaSourceEx + IMFGetService + IKsControl
│  ├─ src/MediaStream.{h,cpp}    IMFMediaStream2; pulls frames from FrameSource
│  ├─ src/FrameSource.{h,cpp}    shared-memory reader + "no signal" fallback pattern
│  ├─ src/SharedFrame.h          shared-memory header layout (shared with C# writer)
│  └─ registrar/main.cpp         console exe: MFCreateVirtualCamera → Start → wait for Enter → Remove
├─ framewriter/                  (C# .NET 10 console)
│  ├─ FrameWriter.csproj
│  ├─ Program.cs                 opens mapping, writes animated NV12 at 30 fps
│  └─ SharedFrameLayout.cs       mirrors SharedFrame.h
├─ usbmux/                       (C# .NET 10)
│  ├─ UsbmuxSpike.csproj         console
│  ├─ UsbmuxProtocol.cs          encode/decode usbmux plist packets
│  ├─ Program.cs                 list devices → connect 47820 → receive one frame → save JPEG
│  └─ tests/UsbmuxSpike.Tests.csproj + UsbmuxProtocolTests.cs
├─ ios-hello/
│  ├─ project.yml                XcodeGen spec
│  └─ Sources/{SpikeApp.swift, CameraGrabber.swift, FrameServer.swift, Info.plist}
.github/workflows/ios-spike.yml  macOS build → unsigned .ipa artifact
docs/spikes/phase0-dev-setup.md  tool install record
docs/spikes/phase0-sidestore-checklist.md   owner steps
docs/spikes/phase0-results.md    outcomes + decisions
```

---

### Task 1: Development environment

**Files:**
- Create: `docs/spikes/phase0-dev-setup.md`

**Interfaces:**
- Produces: working `cmake`, MSVC (`cl.exe` via VS Build Tools 2022 with the "Desktop development with C++" workload + Windows 11 SDK), `dotnet` (SDK 10.x), Apple Mobile Device Service. Later tasks assume these are on PATH or found via `vswhere`.

Current state (checked 2026-11-10): git ✔, gh ✔ (logged in), **dotnet ✘, VS/MSVC ✘, CMake ✘, Apple Mobile Device Service ✘**.

- [ ] **Step 1: Install tools with winget** (the owner approves each UAC prompt)

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id Kitware.CMake -e
winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --add Microsoft.VisualStudio.Component.Windows11SDK.26100"
winget install --id 9NP83LWLPZ9K -s msstore   # Apple Devices (provides Apple Mobile Device Service)
```

- [ ] **Step 2: Verify**

Run (new shell): `dotnet --list-sdks; cmake --version; & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath; Get-Service "Apple Mobile Device Service"`
Expected: a `10.` SDK line, a CMake version, a Build Tools path, service `Running`.

- [ ] **Step 3: Record versions in `docs/spikes/phase0-dev-setup.md` and commit**

```bash
git add docs/spikes/phase0-dev-setup.md
git commit -m "docs(spike): record Phase 0 dev environment"
```

---

### Task 2: Virtual camera fed from shared memory

**Files:**
- Create: everything under `spikes/vcam/`, `spikes/framewriter/`, `spikes/THIRD_PARTY_NOTICES.md`

**Interfaces:**
- Consumes: Task 1 toolchain.
- Produces (the boundary Phase 1 will reuse conceptually), `SharedFrame.h` / `SharedFrameLayout.cs`:
  - Mapping name: `Global\RaaiCamSpikeFrames` (fallback tried if creation fails: `Local\RaaiCamSpikeFrames`. Record which worked.)
  - Event name: `Global\RaaiCamSpikeFrameReady` (auto-reset).
  - Layout (little-endian, packed):
    `struct SharedFrameHeader { uint32 magic = 0x52434631 /*'RCF1'*/; uint32 version = 1; uint32 width; uint32 height; uint32 format /*0x3231564E NV12*/; uint32 stride; uint64 frameCounter; int64 timestampQpc; uint32 consumerActive; uint32 reserved[7]; }` followed by **one** NV12 frame buffer of `stride*height*3/2` bytes for 1920×1080 max.
  - Writer increments `frameCounter` **after** copying the frame, then sets the event. Reader copies if `frameCounter` changed (single buffer is fine for the spike; tearing is acceptable and noted).

- [ ] **Step 1: Port the media source from smourier/VCamSample (MIT)** into `src/Activator`, `src/MediaSource`, `src/MediaStream`, `src/dllmain.cpp`. Keep its license header, rename the CLSID (new GUID) and friendly name "RaaiCam Test", and expose the two NV12 media types from Global Constraints. Replace its frame generator with calls to `FrameSource::GetFrame(IMFSample** out, UINT32 width, UINT32 height)`.

- [ ] **Step 2: Implement `FrameSource`** in `src/FrameSource.cpp`:
  - On first use, `CreateFileMappingW` with a security descriptor from SDDL `D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;IU)` (System, LocalService, Interactive Users), creating the mapping + event above. Log every Win32 error via `OutputDebugStringW` with prefix `[RaaiCamVCam]`.
  - `GetFrame`: if `frameCounter` advanced within the last 500 ms → copy (scaling not needed; the writer writes the requested size from the header's `width/height` set by the reader); else → fill the **"no signal" pattern**: Y=0x60, UV=0x80 with a 64-px white block moving horizontally 4 px/frame (so a frozen app is distinguishable from a live "no signal").
  - Set `consumerActive = 1` while the stream is started, `0` on stop.

- [ ] **Step 3: Implement `registrar/main.cpp`**: `MFStartup`; `MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser, L"RaaiCam Test", L"{<CLSID>}", nullptr, 0, &cam)`; `cam->Start(nullptr)`; print `REGISTERED`; wait for Enter. (The spike uses `Session` lifetime so the camera disappears when the registrar exits. Phase 1 switches to `System` lifetime, per spec §9.3.) `cam->Remove()`; print `REMOVED`. Command `registrar.exe --register-dll` performs `regsvr32`-equivalent HKCU registration of the DLL path first.

- [ ] **Step 4: Implement the C# frame writer** (`spikes/framewriter/Program.cs`): open the mapping and event by name (`MemoryMappedFile.OpenExisting`, `EventWaitHandle.OpenExisting`), read `width/height` from the header, and at 30 fps write an NV12 frame showing color bars plus the frame number drawn as a large moving bar (so lag and freezes are visible). Print `consumerActive` changes to the console. Exit code 3 with the exception message if the mapping can't be opened.

- [ ] **Step 5: Build**

Run: `cmake -S spikes/vcam -B spikes/vcam/build -A x64 && cmake --build spikes/vcam/build --config Release && dotnet build spikes/framewriter -c Release`
Expected: `RaaiCamVCam.dll`, `registrar.exe`, `FrameWriter.dll` built with 0 errors.

- [ ] **Step 6: Verify "writer-absent" first** (Review Focus 1): run `registrar.exe --register-dll`, keep it open, open the **Windows Camera app**, choose "RaaiCam Test".
Expected: grey frame with a moving white block, no crash. Then start `FrameWriter`; expected: color bars within 1 s, console prints `consumerActive=1`. Stop the writer; expected: back to grey pattern within 1 s.

- [ ] **Step 7: Owner app checks** (record pass/fail + screenshot in `docs/spikes/phase0-results.md`):
  1. Zoom desktop → Settings → Video → "RaaiCam Test" animates.
  2. Chrome → `https://webcamtests.com` (or Meet's preview) → animates.
  3. Edge → same.
  4. Discord → Voice & Video → camera preview animates.
  5. Camera app + Chrome at the same time (Review Focus 2/3): note behavior.
  6. Slack huddle camera (optional).

- [ ] **Step 8: Commit**

```bash
git add spikes/vcam spikes/framewriter spikes/THIRD_PARTY_NOTICES.md docs/spikes/phase0-results.md
git commit -m "spike: MF virtual camera fed from shared memory"
```

---

### Task 3: iOS spike app + cloud build

**Files:**
- Create: `spikes/ios-hello/project.yml`, `spikes/ios-hello/Sources/*`, `.github/workflows/ios-spike.yml`

**Interfaces:**
- Produces: an unsigned `RaaiCamSpike.ipa` workflow artifact; on device, a TCP server on port 47820 that, per connection, sends **one** frame: `[u32 big-endian length][JPEG bytes]` then closes. Task 4 consumes exactly this.

- [ ] **Step 1: `project.yml`** (XcodeGen): target `RaaiCamSpike`, platform iOS, deploymentTarget `17.0`, bundle id `com.raaicam.spike`, sources `Sources`, Info.plist `Sources/Info.plist` with `NSCameraUsageDescription = "RaaiCam uses the camera to stream video to your PC."`, `NSLocalNetworkUsageDescription = "RaaiCam connects to your PC over Wi-Fi."`, `UIRequiresFullScreen = YES`, portrait only.

- [ ] **Step 2: Swift sources**
  - `SpikeApp.swift`: SwiftUI app; screen shows "RaaiCam Spike", camera permission state, listener state (`ready on 47820` / error), count of frames served; sets `UIApplication.shared.isIdleTimerDisabled = true`.
  - `CameraGrabber.swift`: `final class CameraGrabber` with `func latestJPEG() -> Data?`; `AVCaptureSession` preset `.hd1920x1080`, back `.builtInWideAngleCamera`, `AVCaptureVideoDataOutput` keeping the latest `CVPixelBuffer`, encoding to JPEG (quality 0.8) on demand via `CIContext.jpegRepresentation`.
  - `FrameServer.swift`: `final class FrameServer` with `init(port: UInt16 = 47820, grabber: CameraGrabber)`, `func start()`; `NWListener(using: .tcp, on: 47820)`; on each connection send the length-prefixed JPEG (or length 0 if no frame yet), then cancel the connection.

- [ ] **Step 3: Workflow `.github/workflows/ios-spike.yml`**: `on: [workflow_dispatch, push (paths: spikes/ios-hello/**)]`; `runs-on: macos-15`; steps: checkout → `brew install xcodegen` → `xcodegen generate --spec spikes/ios-hello/project.yml` → `xcodebuild -project spikes/ios-hello/RaaiCamSpike.xcodeproj -scheme RaaiCamSpike -configuration Release -sdk iphoneos -derivedDataPath build CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build` → package: `mkdir Payload && cp -r build/Build/Products/Release-iphoneos/RaaiCamSpike.app Payload/ && zip -r RaaiCamSpike.ipa Payload` → `actions/upload-artifact@v4` name `RaaiCamSpike-ipa`.

- [ ] **Step 4: Owner gate: GitHub repo.** Ask the owner in chat to approve creating the **public** repo (proposed name `RaaiCam`) under their connected account and pushing `main`. Only after a clear yes: `gh repo create RaaiCam --public --source . --push`.

- [ ] **Step 5: Trigger and verify the build**

Run: `gh workflow run ios-spike.yml && gh run watch --exit-status`
Expected: run succeeds; `gh run download --name RaaiCamSpike-ipa -D spikes/out` yields `RaaiCamSpike.ipa` (~1–3 MB). If it fails, fix from the run log (`gh run view --log-failed`) and re-run.

- [ ] **Step 6: Commit** (the push in Step 4 already sent earlier commits; further pushes also need the owner's OK)

```bash
git add spikes/ios-hello .github/workflows/ios-spike.yml
git commit -m "spike: iOS frame server app + unsigned ipa CI build"
```

---

### Task 4: USB tunnel client (usbmux)

**Files:**
- Create: `spikes/usbmux/*`, `spikes/usbmux/tests/*`

**Interfaces:**
- Consumes: Task 3 on-device protocol (port 47820, `[u32 BE length][JPEG]`).
- Produces (validated wire format for Phase 1's `UsbmuxClient`), in `UsbmuxProtocol.cs`:
  - `static byte[] EncodePlistPacket(uint tag, IDictionary<string, object> plist)`: 16-byte header `[u32 LE totalLength][u32 LE version=1][u32 LE type=8][u32 LE tag]` + XML plist body.
  - `static (uint Tag, Dictionary<string, object> Plist) DecodePacket(ReadOnlySpan<byte> packet)`
  - `static Dictionary<string, object> ListDevicesRequest()` → `{MessageType:"ListDevices", ClientVersionString:"raaicam-spike", ProgName:"raaicam-spike"}`
  - `static Dictionary<string, object> ConnectRequest(int deviceId, ushort port)` → `{MessageType:"Connect", DeviceID:deviceId, PortNumber: (int)BinaryPrimitives.ReverseEndianness(port)}` (port in network byte order, as usbmuxd expects)
  - `enum UsbmuxResult { Ok = 0, BadCommand = 1, BadDevice = 2, ConnectionRefused = 3, BadVersion = 6 }` and `static UsbmuxResult ParseResult(Dictionary<string, object> plist)`.

- [ ] **Step 1: Write failing tests** in `UsbmuxProtocolTests.cs`:

```csharp
[Fact] public void Header_IsLittleEndian_WithPlistType()
{
    var pkt = UsbmuxProtocol.EncodePlistPacket(7, UsbmuxProtocol.ListDevicesRequest());
    Assert.Equal((uint)pkt.Length, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(0)));
    Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(4)));
    Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(8)));
    Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(12)));
}
[Fact] public void ConnectRequest_PortIsNetworkByteOrder()
    => Assert.Equal(52410, (int)UsbmuxProtocol.ConnectRequest(5, 47820)["PortNumber"]); // 47820 = 0xBACC → byte-swapped 0xCCBA = 52410
[Fact] public void RoundTrip_PreservesTagAndKeys() { /* encode ConnectRequest(3, 47820) with tag 9, decode, assert Tag==9, DeviceID==3, MessageType=="Connect" */ }
[Theory, InlineData(0, UsbmuxResult.Ok), InlineData(3, UsbmuxResult.ConnectionRefused), InlineData(2, UsbmuxResult.BadDevice)]
public void ParseResult_MapsNumbers(int n, UsbmuxResult expected) { /* plist {MessageType:"Result", Number:n} */ }
```

- [ ] **Step 2: Run tests and confirm they fail**

Run: `dotnet test spikes/usbmux/tests`
Expected: FAIL (compile errors: `UsbmuxProtocol` not defined).

- [ ] **Step 3: Implement `UsbmuxProtocol.cs`** with the signatures above. Plist (de)serialization with `System.Xml.Linq`, supporting only `dict`, `string`, `integer`, `array`, `data`, `true/false` (enough for usbmuxd replies).

- [ ] **Step 4: Run tests and confirm they pass**

Run: `dotnet test spikes/usbmux/tests`
Expected: PASS (5 tests).

- [ ] **Step 5: Implement `Program.cs`**: TCP connect `127.0.0.1:27015` (on failure print "Apple Mobile Device Service not reachable. Install the Apple Devices app." and exit 1); send `ListDevices`; print each device's `DeviceID`, `ConnectionType`, `SerialNumber`; pick the first `USB` device; on a **new** socket send `Connect(deviceId, 47820)`; on `ConnectionRefused` print "Phone found, but the RaaiCam Spike app isn't open." and exit 2 (Review Focus 4); on `Ok`, read `[u32 BE length]` + JPEG, save it to `spikes/out/frame.jpg`, print the byte count and round-trip ms.

- [ ] **Step 6: Owner hardware check** (after Task 5 installed the app): phone plugged in, app open → run `dotnet run --project spikes/usbmux`.
Expected: device listed; `frame.jpg` is a real 1920×1080 camera photo; printed time < 300 ms. Also run with the app closed → exit 2 with the message.

- [ ] **Step 7: Commit**

```bash
git add spikes/usbmux
git commit -m "spike: usbmux client fetches a frame from the iPhone over USB"
```

---

### Task 5: SideStore install (owner checklist)

**Files:**
- Create: `docs/spikes/phase0-sidestore-checklist.md`

**Interfaces:**
- Consumes: `RaaiCamSpike.ipa` from Task 3.
- Produces: the spike app running on the owner's iPhone; recorded iOS version, steps that caused trouble, and the expiry date shown in SideStore.

- [ ] **Step 1: Write the checklist** with current official links only (sidestore.io docs, iLoader release page): (1) install iLoader on Windows; (2) connect the iPhone by USB, trust the computer; (3) enable Developer Mode on the iPhone (Settings → Privacy & Security); (4) in iLoader, sign in with the free Apple ID **in iLoader itself** (Claude never handles the password); install SideStore + pairing file; (5) install LocalDevVPN/StosVPN as SideStore docs say for on-device refresh; (6) copy `RaaiCamSpike.ipa` to the phone (Files app via iCloud Drive/USB) and install from SideStore → "+"; (7) open the app, allow camera; (8) note the app expiry date; (9) test SideStore "Refresh All" works on Wi-Fi with the PC off.

- [ ] **Step 2: Owner runs it**; Claude records results in `docs/spikes/phase0-results.md` (iOS version, pass/fail per step, refresh works without PC yes/no, behavior after locking the screen per Review Focus 5).

- [ ] **Step 3: Commit**

```bash
git add docs/spikes/phase0-sidestore-checklist.md docs/spikes/phase0-results.md
git commit -m "docs(spike): SideStore install checklist and results"
```

---

### Task 6: Results report & decisions

**Files:**
- Modify: `docs/spikes/phase0-results.md`
- Modify: `docs/superpowers/specs/2026-11-10-raaicam-design.md` (§3 decisions log, §14 risks, §15 open items)

**Interfaces:**
- Consumes: outcomes of Tasks 2–5.
- Produces: decisions that the Phase 1 plan is written from.

- [ ] **Step 1: Fill the report** with a table: probe · result · evidence (screenshot/log path) · implication. Required decisions:
  - **D17** shared-memory naming that worked (`Global\` vs `Local\`) and the DACL used.
  - **D18** DirectShow fallback needed? (yes if any of Zoom / Chrome / Edge / Discord failed to show the camera; name which).
  - **D19** iOS build pipeline confirmed (runner image, XcodeGen version, build minutes used).
  - **D20** install route confirmed (SideStore PC-less refresh works? else AltServer).
  - **D21** USB tunnel confirmed (round-trip ms, any Apple Devices app quirks).
- [ ] **Step 2: Update the spec** decisions log/risks with D17–D21; close resolved open items.
- [ ] **Step 3: Commit**

```bash
git add docs/spikes/phase0-results.md docs/superpowers/specs/2026-11-10-raaicam-design.md
git commit -m "docs: Phase 0 results and decisions"
```

- [ ] **Step 4: Gate:** owner reviews the results. Next step is writing the Phase 1 plan (`docs/superpowers/plans/<date>-phase1-basic-webcam.md`).

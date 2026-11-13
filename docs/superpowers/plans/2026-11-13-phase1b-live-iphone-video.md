# Phase 1B — Live iPhone Video over USB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Real camera video from the owner's iPhone, over the USB cable, into the "RaaiCam" virtual camera: the production iPhone app (capture + hardware H.264 + approved dim UI), protocol v1, and a PC host that decodes and feeds `VirtualCameraSink`, starting/stopping the phone camera with the meeting app.

**Architecture:** iPhone app (`ios/`, Swift, no dependencies) listens on TCP 47820 (BSD socket, loopback peers only in 1B). The PC (`windows/`) reaches it through Apple's usbmux tunnel (`RaaiCam.Core`), speaks protocol v1 (`RaaiCam.Protocol`), decodes H.264 with the Media Foundation decoder (`RaaiCam.Media.H264Decoder`) and writes NV12 into the Phase 1A virtual camera. A console host (`raaicam-host`) wires it together until the tray app arrives in 1C. Wi-Fi, pairing/encryption and failover are Phase 1C.

**Tech Stack:** Swift 5.10+/SwiftUI/AVFoundation/VideoToolbox, XcodeGen, GitHub Actions `macos-15` (unsigned .ipa, simulator unit tests); C# .NET 10, xUnit, Vortice.MediaFoundation (pinned exact version), Phase 1A `RaaiCam.Media`.

**Spec:** `docs/superpowers/specs/2026-11-10-raaicam-design.md` (FR-STR-1,2,3,5,6; FR-PWR-1,3; FR-CAM-1 lens; §7 iPhone screens; §9.2; §9.4; D19–D22) · contract `protocol/SHARED_FRAME.md` · Phase 0 results `docs/spikes/phase0-results.md`.

## Global Constraints

- **Phone orientation:** landscape on the stand (owner decision, 2026-11-13). Output is always upright landscape 16:9 using `AVCaptureDevice.RotationCoordinator.videoRotationAngleForHorizonLevelCapture` (works for both landscape directions).
- **Phone UI:** the final approved dim UI now (owner decision): Ready / Streaming (near-black, tiny red dot + status) / Warnings, dim after 10 s, `isIdleTimerDisabled = true`, tap to show quick controls (Pause camera, Switch lens).
- **iOS:** deployment target **17.0**, bundle id **`com.raaicam.app`**, display name **"RaaiCam"**, rear wide (default) and ultra-wide lenses only, no third-party code, camera + local-network usage strings.
- **Listener:** BSD socket, TCP **47820**, bound to `INADDR_ANY`, but every accepted connection whose peer address is not loopback (127.0.0.1 / ::1) is closed immediately (USB-only in 1B; D22 forbids unauthenticated Wi-Fi video). One client at a time; a new loopback client replaces the old one.
- **Capture size = consumer size:** the PC sends `start` with the size the meeting app negotiated (1920×1080 or 1280×720, 30 fps); the phone captures with the matching preset and encodes at exactly that size, so the PC never scales.
- **Encoder (FR-STR-3):** H.264 High, `RealTime=true`, `AllowFrameReordering=false`, keyframe every 60 frames and on request, average bitrate default **8000 kbps** (USB), `DataRateLimits` 1.5× average per second, Annex-B output.
- **Camera lifecycle (FR-STR-6):** the phone camera runs only between `start` and `stop`; the PC sends `stop` within 1 s of `consumerActive` → 0 and on disconnect the phone stops by itself.
- **Timestamps:** frames written to the sink are stamped with host `Stopwatch.GetTimestamp()` at write time (SHARED_FRAME.md contract). The phone's capture time is used only for latency stats.
- **Latency target:** capture→sink-write p95 ≤ **100 ms** over USB (FR-STR-5, measured via PING/PONG clock offset).
- **Builds:** iOS only via `.github/workflows/ios.yml` on `macos-15` (repo is public → free minutes); never commit `.ipa`, build output, probe images, SIDs, names, emails.
- **Windows build:** `windows/build.ps1 -Test` (VS 2026 Build Tools at `D:\VS\BuildTools`, SDK `D:\WindowsKits`). C: has ~2 GB free: install nothing large.
- **Packages:** exact pinned versions only; no floating versions.
- **Install on phone:** owner runs iloader "Import IPA" (SideStore sign-in is broken upstream, D20).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; never push without the owner's OK.

## Review Focus

1. **Phone app backgrounded or screen locked mid-stream:** iOS suspends the app and the socket dies. Expected: the meeting shows the card within 1 s, the host keeps retrying, and video resumes within 3 s of reopening the app. Tests: Task 5 `Session_PeerClosed_RaisesDisconnected`; Task 8 owner step 4.
2. **USB unplugged / replugged:** usbmux reports detach. Expected: card, no host crash, automatic reconnect within 3 s of replug. Tests: Task 4 `Listen_ParsesAttachedAndDetached`; Task 7 `Host_ReconnectsAfterDetach` (fake transport); Task 8 owner step 5.
3. **Meeting app switches 1080p ↔ 720p mid-stream:** the host restarts the phone stream at the new size; no frame of the wrong size reaches the sink (WriteFrame returns false otherwise). Tests: Task 7 `Host_RestartsPhoneOnSizeChange`; Task 8 probe check C2.
4. **Corrupt / truncated video or decoder error:** no crash; the host requests a keyframe and recovers within 2 s. Tests: Task 6 `Decoder_CorruptAccessUnit_NoThrow_RequestsKeyframe`; Task 5 `Codec_RejectsOversizedLength`.
5. **Phone overheating:** thermal `.serious`/`.critical` is reported in telemetry, shown on the phone's warning screen and in the host status line. Tests: Task 2 `TelemetryEncodesThermalState`; Task 3 UI state test `WarningShownForSeriousThermal`.

---

## Protocol v1 (normative: `protocol/PROTOCOL.md`)

All integers **big-endian**. Frame: `[u8 type][u32 length][payload]`, `length` = payload bytes, max **4 MiB**; larger → protocol error, close.

| type | name | dir | payload |
|---|---|---|---|
| 0x01 | HELLO | both | UTF-8 JSON `{"proto":1,"role":"phone"\|"pc","app":"RaaiCam","version":"x.y.z","device":"…","os":"…"}` — first frame each way; proto ≠ 1 → close |
| 0x02 | AUTH | — | reserved for 1C (receiving it in 1B → ignore) |
| 0x03 | CMD | pc→phone | UTF-8 JSON, see commands |
| 0x04 | TELEMETRY | phone→pc | UTF-8 JSON every 2 s |
| 0x05 | LOG | phone→pc | UTF-8 text line (no newline), level prefix `I `/`W `/`E ` |
| 0x06 | VIDEO | phone→pc | `[u64 captureUs][u32 seq][u8 flags][Annex-B access unit]`, flags bit0 = keyframe (contains SPS+PPS+IDR) |
| 0x07 | PING | pc→phone | `[u64 pcSendUs]` |
| 0x08 | PONG | phone→pc | `[u64 pcSendUs][u64 phoneRecvUs]` |

Commands (`"cmd"` field): `{"cmd":"start","width":1920,"height":1080,"fps":30,"lens":"wide","bitrateKbps":8000}` · `{"cmd":"stop"}` · `{"cmd":"keyframe"}` · `{"cmd":"bitrate","kbps":6000}` · `{"cmd":"lens","lens":"wide"|"ultra"}`. Unknown cmd → phone replies LOG `W unknown cmd <name>`.

Telemetry: `{"thermal":"nominal"|"fair"|"serious"|"critical","battery":0.0-1.0,"charging":bool,"streaming":bool,"width":int,"height":int,"fpsCapture":float,"fpsEncode":float,"bitrateKbps":int,"lens":"wide"|"ultra","paused":bool}`.

Clocks: `captureUs`/`phoneRecvUs` = phone `mach_continuous_time` in µs; `pcSendUs` = PC `Stopwatch` µs. Offset estimate per PONG = `phoneRecvUs − (pcSendUs + rtt/2)`; keep the sample with the smallest RTT in the last 10 s.

`protocol/test-vectors.json`: hex encodings of one frame of every type (incl. a VIDEO with a 16-byte dummy AU) shared by the C# and Swift tests.

---

## File Structure

```
protocol/PROTOCOL.md, protocol/test-vectors.json
.github/workflows/ios.yml                      xcodegen → unit tests (simulator) → unsigned RaaiCam.ipa artifact
ios/
├─ project.yml                                 targets: RaaiCam (app), RaaiCamTests (unit)
├─ Sources/
│  ├─ App/RaaiCamApp.swift, AppModel.swift     state machine: ready / streaming / paused / warning
│  ├─ UI/ReadyView.swift, StreamingView.swift, WarningView.swift, QuickControls.swift, Dimmer.swift
│  ├─ Protocol/Frame.swift, FrameCodec.swift, Messages.swift      pure Swift, unit-tested
│  ├─ Net/FrameServer.swift                    BSD socket 47820, loopback-only peers, one client
│  ├─ Media/CaptureController.swift            AVCaptureSession, preset by size, lens, rotation coordinator
│  ├─ Media/H264Encoder.swift, AnnexB.swift    VTCompressionSession; AVCC→Annex-B (unit-tested)
│  ├─ Session/PhoneSession.swift               HELLO, CMD handling, VIDEO/TELEMETRY/LOG/PONG, stop on disconnect
│  └─ Info.plist
└─ Tests/ FrameCodecTests.swift, AnnexBTests.swift, AppModelTests.swift
windows/
├─ RaaiCam.Protocol/  FrameCodec.cs, Messages.cs (+ .Tests)
├─ RaaiCam.Core/      Usbmux/UsbmuxClient.cs, Usbmux/Plist.cs, Phone/PhoneSession.cs, Phone/ClockSync.cs (+ .Tests)
├─ RaaiCam.Media/     H264Decoder.cs (+ tests in RaaiCam.Media.Tests)
└─ tools/raaicam-host/ Program.cs, Host.cs     sink consumer state ↔ phone session ↔ decoder ↔ sink
docs/checklists/phase-1b.md
```

---

### Task 1: Protocol v1 spec, test vectors, C# codec

**Files:**
- Create: `protocol/PROTOCOL.md`, `protocol/test-vectors.json`, `windows/RaaiCam.Protocol/{RaaiCam.Protocol.csproj, FrameCodec.cs, Messages.cs}`
- Test: `windows/RaaiCam.Protocol.Tests/{csproj, FrameCodecTests.cs}`; add both projects to `windows/RaaiCam.sln`

**Interfaces:**
- Produces:
  - `enum FrameType : byte { Hello=1, Auth=2, Cmd=3, Telemetry=4, Log=5, Video=6, Ping=7, Pong=8 }`
  - `readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload)`
  - `static class FrameCodec { const int MaxPayload = 4*1024*1024; static void Write(Stream s, FrameType t, ReadOnlySpan<byte> payload); static async ValueTask<Frame?> ReadAsync(Stream s, CancellationToken ct); /* null on clean EOF at a frame boundary; throws ProtocolException on oversize/truncation */ }`
  - `sealed class ProtocolException : Exception`
  - `static class Messages`: `byte[] Hello(string role, string version, string device, string os)`, `HelloInfo ParseHello(ReadOnlySpan<byte>)`, `byte[] Cmd(object cmd)` (System.Text.Json, camelCase), `StartCmd`, `StopCmd`, `KeyframeCmd`, `BitrateCmd`, `LensCmd` records, `Telemetry ParseTelemetry(ReadOnlySpan<byte>)` (record with the telemetry fields), `byte[] Ping(ulong pcSendUs)`, `(ulong pcSendUs, ulong phoneRecvUs) ParsePong(ReadOnlySpan<byte>)`, `VideoFrame ParseVideo(ReadOnlyMemory<byte>)` → `record VideoFrame(ulong CaptureUs, uint Seq, bool Keyframe, ReadOnlyMemory<byte> AccessUnit)`.

- [ ] **Step 1: Write `PROTOCOL.md`** (the Protocol v1 section above, verbatim, plus a Versioning note) and `test-vectors.json`: `{"frames":[{"name":"hello-pc","type":1,"payloadUtf8":"…","hex":"01 0000004c …"}, …]}` for HELLO, CMD start, CMD stop, TELEMETRY, LOG, VIDEO (captureUs 0x0102030405060708, seq 42, keyframe, AU `00 00 00 01 67 …` 16 bytes), PING (pcSendUs 1000000), PONG (1000000, 2000000). Generate hex with a tiny throwaway script, then check one by hand.
- [ ] **Step 2: Failing tests**

```csharp
[Fact] public void Vectors_RoundTrip() { /* for every vector: Write(type,payload) bytes == hex; ReadAsync(hex) == (type,payload) */ }
[Fact] public async Task Read_NullOnCleanEof() { }
[Fact] public async Task Read_ThrowsOnTruncatedPayload() { /* header says 10, 4 bytes then EOF */ }
[Fact] public async Task Codec_RejectsOversizedLength() { /* length 4 MiB + 1 → ProtocolException, nothing allocated (assert via a stream that throws if read past header) */ }
[Fact] public void Video_ParsesHeaderAndKeyframeFlag() { }
[Fact] public void Pong_ParsesBothTimes() { }
[Fact] public void Hello_RejectsWrongProto() { /* proto 2 → ProtocolException */ }
[Fact] public void Cmd_StartSerializesExactJson() { /* == {"cmd":"start","width":1920,"height":1080,"fps":30,"lens":"wide","bitrateKbps":8000} */ }
```

- [ ] **Step 3:** `dotnet test windows/RaaiCam.Protocol.Tests` → FAIL (compile).
- [ ] **Step 4: Implement** per Interfaces.
- [ ] **Step 5:** `dotnet test windows/RaaiCam.Protocol.Tests` → 8 PASS.
- [ ] **Step 6: Commit** `feat(protocol): protocol v1 spec, test vectors and C# codec`.

---

### Task 2: iOS app core — protocol, server, capture, encoder, session, CI

**Files:**
- Create: everything under `ios/` except `UI/` and `AppModel.swift` (Task 3), `.github/workflows/ios.yml`
- Modify: none in `spikes/` (keep the spike workflow as is)

**Interfaces:**
- Consumes: `protocol/PROTOCOL.md`, `protocol/test-vectors.json` (copied into the test bundle as a resource by `project.yml`).
- Produces (used by Task 3):
  - `final class PhoneSession: ObservableObject` with `@Published var state: SessionState` (`.waiting`, `.connected`, `.streaming(width:height:)`, `.error(String)`), `@Published var telemetry: TelemetrySnapshot`, `func start()`, `func setPaused(_ paused: Bool)` (paused → capture stopped, VIDEO stops, telemetry `paused=true`), `func switchLens(_ lens: Lens)`.
  - `enum Lens: String { case wide, ultra }`, `struct TelemetrySnapshot` (all telemetry fields).
- Behavior:
  - FrameServer: BSD socket per Global Constraints; `SO_NOSIGPIPE`; reads frames on a background thread; writes serialized through one serial queue; VIDEO frames dropped (not queued) if more than 3 frames are pending to send (latency first) — log once per 5 s.
  - Session: sends HELLO on connect; on `start` → configure capture preset (`.hd1920x1080` / `.hd1280x720`), lens, rotation, 30 fps, start encoder at that size; on `stop`/disconnect → stop capture + encoder; on `keyframe` → force next IDR (`kVTEncodeFrameOptionKey_ForceKeyFrame`); on `bitrate` → update `AverageBitRate` + limits; on `lens` → switch input inside `beginConfiguration` (keep streaming); PING → PONG immediately (off the video queue); TELEMETRY every 2 s while connected; forward own log lines as LOG.
  - Encoder: VTCompressionSession H.264 High per Global Constraints; convert AVCC NAL units to Annex-B; prepend SPS/PPS on keyframes; `captureUs` = sample PTS converted to `mach_continuous_time` µs.
- CI `ios.yml`: on push to `ios/**`, `protocol/**`, workflow_dispatch; `macos-15`; `brew install xcodegen`; `xcodegen generate --spec ios/project.yml`; `xcodebuild test -scheme RaaiCam -destination 'platform=iOS Simulator,name=iPhone 16,OS=latest'` (unit tests only; no camera on simulator); `xcodebuild build … -sdk iphoneos CODE_SIGNING_ALLOWED=NO …`; package `RaaiCam.ipa`; upload artifact `RaaiCam-ipa`; `permissions: contents: read`.

- [ ] **Step 1: Failing Swift tests** in `ios/Tests/`:

```swift
func testVectorsRoundTrip()            // every vector encodes/decodes byte-exact
func testDecoderRejectsOversize()      // length > 4 MiB → error, no allocation
func testPartialFramesReassemble()     // feed bytes 1 at a time → one complete frame
func testAnnexBFromAvcc()              // [00 00 00 05 | 5 bytes NAL] → [00 00 00 01 | 5 bytes]
func testAnnexBKeyframePrependsParameterSets()
func testStartCommandParses()          // {"cmd":"start",...} → StartCommand(w:1920,h:1080,fps:30,lens:.wide,kbps:8000)
func testTelemetryEncodesThermalState() // ProcessInfo.ThermalState.serious → "serious"
```

- [ ] **Step 2: Push and confirm RED on CI** (pushing the `phase1b-live-video` branch for CI requires the owner's OK, obtained at plan approval; push only this branch, never force-push): `gh run watch` → tests fail to compile.
- [ ] **Step 3: Implement** Protocol, AnnexB, FrameServer, CaptureController, H264Encoder, PhoneSession, a minimal `RaaiCamApp` that shows `Text(state)` (Task 3 replaces it), `project.yml`, `ios.yml`.
- [ ] **Step 4: CI GREEN** — `gh run watch <id> --exit-status`: tests pass, `RaaiCam-ipa` artifact exists; download to `spikes/out/RaaiCam.ipa` (gitignored) and confirm `Payload/RaaiCam.app/Info.plist` has `CFBundleIdentifier = com.raaicam.app`.
- [ ] **Step 5: Commit** (each fix commit is fine) `feat(ios): production app core — protocol, loopback server, capture, H.264 encoder, CI`.

(Device behavior is verified end-to-end in Task 8 once the PC side exists.)

---

### Task 3: iOS approved dim UI and app state

**Files:**
- Create: `ios/Sources/App/AppModel.swift`, `ios/Sources/UI/*.swift`, `ios/Tests/AppModelTests.swift`
- Modify: `ios/Sources/App/RaaiCamApp.swift`

**Interfaces:**
- Consumes: `PhoneSession`, `TelemetrySnapshot`, `Lens` (Task 2).
- Produces: `final class AppModel: ObservableObject` — `screen: Screen` (`.ready`, `.streaming`, `.warning(WarningKind)`), `isDimmed: Bool`, `showControls: Bool`; pure function `static func screen(for state: SessionState, telemetry: TelemetrySnapshot, cameraAuthorized: Bool) -> Screen`; `WarningKind` = `.cameraPermission`, `.thermal(serious/critical)`, `.disconnected`, `.notCharging` (FR-PWR-4: streaming && !charging && battery < 0.30).

Screens (match the approved mockup `.superpowers/brainstorm/*/content/phone-screens.html`, local only): black backgrounds, SF Symbols, large text; **Ready**: dim icon, "Ready", "Camera starts when a meeting app needs it", pills "🔌 USB · connected/waiting", "🔋 battery% · thermal"; **Streaming**: near-black, tiny red dot + "Live · <w>p · <thermal>", "Tap to show details"; **Warning**: amber icon, title, one-line advice. Tap anywhere → `QuickControls` sheet: Pause camera (toggle), Switch lens (wide/ultra), close. Dimming: `UIScreen.main.brightness` → 0.05 after 10 s without touches, restored on touch and on app background; `isIdleTimerDisabled = true` while the app is active.

- [ ] **Step 1: Failing tests** `AppModelTests`: `testReadyWhenWaiting`, `testStreamingWhenStreaming`, `testWarningShownForSeriousThermal`, `testCriticalThermalWins`, `testNotChargingLowBatteryWarning`, `testPermissionDeniedWarning`.
- [ ] **Step 2:** push → CI RED.
- [ ] **Step 3: Implement** AppModel + views + dimmer.
- [ ] **Step 4:** CI GREEN, artifact updated.
- [ ] **Step 5: Commit** `feat(ios): approved dim UI — ready, streaming, warnings, quick controls`.

---

### Task 4: Production usbmux client

**Files:**
- Create: `windows/RaaiCam.Core/{RaaiCam.Core.csproj, Usbmux/Plist.cs, Usbmux/UsbmuxClient.cs}`
- Test: `windows/RaaiCam.Core.Tests/{csproj, UsbmuxClientTests.cs}`; add to sln

**Interfaces:**
- Consumes: Phase 0 spike `spikes/usbmux/` as reference (wire format proven on the owner's device).
- Produces:
  - `record UsbDevice(int DeviceId, string SerialNumber)`
  - `sealed class UsbmuxClient { UsbmuxClient(Func<CancellationToken, Task<Stream>>? connectDaemon = null /* default TCP 127.0.0.1:27015 */); Task<IReadOnlyList<UsbDevice>> ListDevicesAsync(CancellationToken); Task<Stream> ConnectAsync(int deviceId, ushort port, CancellationToken) /* throws UsbmuxException(Result) */; IAsyncEnumerable<UsbmuxEvent> ListenAsync(CancellationToken) }`
  - `abstract record UsbmuxEvent; record Attached(UsbDevice Device) : UsbmuxEvent; record Detached(int DeviceId) : UsbmuxEvent`
  - `enum UsbmuxResult { Ok=0, BadCommand=1, BadDevice=2, ConnectionRefused=3, BadVersion=6 }`, `sealed class UsbmuxException(UsbmuxResult result)`; daemon unreachable → `UsbmuxUnavailableException`.
  - Only USB devices (`ConnectionType == "USB"`) are returned/raised.
- Bounds: packet length 16..1 MiB (from the spike review).

- [ ] **Step 1: Failing tests** (fake daemon = in-memory duplex stream scripted with real-shaped XML plists):
  `ListDevices_ParsesRealisticReply` (nested `DeviceList` → array of dicts with `DeviceID` + `Properties{ConnectionType,SerialNumber}`, includes one `Network` device that must be filtered), `Connect_SendsNetworkOrderPort` (47820 → 52410), `Connect_RefusedThrows` (Result 3), `Listen_ParsesAttachedAndDetached`, `Packet_RejectsBadLength`, `Daemon_UnavailableThrowsTypedException`.
- [ ] **Step 2:** `dotnet test windows/RaaiCam.Core.Tests` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** → 6 PASS.
- [ ] **Step 5: Commit** `feat(core): production usbmux client with device listen`.

---

### Task 5: PC phone session + clock sync

**Files:**
- Create: `windows/RaaiCam.Core/Phone/{PhoneSession.cs, ClockSync.cs}`
- Test: `windows/RaaiCam.Core.Tests/{PhoneSessionTests.cs, ClockSyncTests.cs}`

**Interfaces:**
- Consumes: `FrameCodec`, `Messages` (Task 1).
- Produces:
  - `sealed class ClockSync { void AddSample(ulong pcSendUs, ulong phoneRecvUs, ulong pcRecvUs); long? OffsetUs { get; } /* phone − pc, from min-RTT sample in last 10 s */; long? ToPcUs(ulong phoneUs); double? RttMs { get; } }`
  - `sealed class PhoneSession : IAsyncDisposable { PhoneSession(Stream transport, Func<ulong> pcNowUs, ILogSink log); Task RunAsync(CancellationToken) /* HELLO exchange, then read loop + PING every 1 s */; Task StartAsync(int w, int h, string lens = "wide", int kbps = 8000); Task StopAsync(); Task RequestKeyframeAsync(); Task SetBitrateAsync(int kbps); Task SetLensAsync(string lens); event Action<VideoFrame>? Video; event Action<Telemetry>? TelemetryReceived; event Action<string>? Disconnected; HelloInfo? Phone { get; } ClockSync Clock { get; } }`
  - `interface ILogSink { void Write(string source, string line); }` + `FileLogSink` writing to `%LOCALAPPDATA%\RaaiCam\logs\phone-YYYYMMDD.log` (rolling 7 days; spec §9.5).
  - Writes are serialized; `Disconnected` fires exactly once (EOF, protocol error, or dispose) with a reason.

- [ ] **Step 1: Failing tests** with an in-memory fake phone:
  `Session_SendsHelloFirst_AndParsesPhoneHello`, `Session_StartSendsExactJson`, `Session_VideoEventCarriesAccessUnit`, `Session_PingEverySecond_UpdatesClock` (fake clock), `Session_PeerClosed_RaisesDisconnected`, `Session_ProtocolError_DisconnectsOnce`, `Session_LogLinesGoToSink`, `ClockSync_UsesMinRttSample`, `ClockSync_ForgetsSamplesOlderThan10s`.
- [ ] **Step 2:** FAIL → **Step 3:** implement → **Step 4:** 9 PASS.
- [ ] **Step 5: Commit** `feat(core): phone session with clock sync and log sink`.

---

### Task 6: H.264 decoder (Media Foundation)

**Files:**
- Create: `windows/RaaiCam.Media/H264Decoder.cs` (+ `Vortice.MediaFoundation` exact-version PackageReference)
- Test: `windows/RaaiCam.Media.Tests/H264DecoderTests.cs`

**Interfaces:**
- Consumes: `TestPatternSource` (Phase 1A) for test input.
- Produces: `sealed class H264Decoder : IDisposable { H264Decoder(int width, int height); /* CLSID_MSH264DecoderMFT, CPU/system-memory NV12 output, CODECAPI_AVLowLatencyMode=1 */ DecodeResult Decode(ReadOnlySpan<byte> annexBAccessUnit, long sampleTime100ns, Span<byte> nv12Out, out int outStride); event Action? KeyframeNeeded; }` with `enum DecodeResult { Frame, NeedMoreInput, Error }`. Output is cropped to exactly `width×height` (the MFT may pad to 1088 rows — copy only the visible rows, UV plane from the padded offset). On `Error` or an AU before the first keyframe → raise `KeyframeNeeded` (at most once per 500 ms) and return `Error`/`NeedMoreInput` without throwing. Handles `MF_E_TRANSFORM_STREAM_CHANGE` by renegotiating the output type.
- Test helper (test project only): `TestH264Encoder` using the MF H.264 **encoder** MFT to produce Annex-B AUs from `TestPatternSource` frames (no committed binary fixtures).

- [ ] **Step 1: Failing tests:** `Decoder_RoundTrip_FrameIdsSurvive` (encode 30 pattern frames at 1280×720 @ 8 Mbps → decode → `TestPatternSource.DecodeId` of the top row equals the source id for ≥ 28 frames), `Decoder_OutputIsExactSize_1080p` (no padding rows leak: last Y row is pattern, not garbage), `Decoder_CorruptAccessUnit_NoThrow_RequestsKeyframe`, `Decoder_DeltaBeforeKeyframe_RequestsKeyframe`.
- [ ] **Step 2:** FAIL → **Step 3:** implement → **Step 4:** 4 PASS (+ all earlier Media tests).
- [ ] **Step 5: Commit** `feat(media): Media Foundation H.264 decoder with keyframe recovery`.

---

### Task 7: `raaicam-host` — phone → decoder → virtual camera

**Files:**
- Create: `windows/tools/raaicam-host/{raaicam-host.csproj, Program.cs, Host.cs}`, `windows/tools/raaicam-host/Abstractions.cs`
- Test: `windows/RaaiCam.Core.Tests/HostTests.cs` (Host lives in a testable class with injected fakes; reference the host project or move `Host.cs` into `RaaiCam.Core/Hosting/`)

**Interfaces:**
- Consumes: `VirtualCameraSink` + `ConsumerState` (1A), `UsbmuxClient` (T4), `PhoneSession` (T5), `H264Decoder` (T6), `FramePacer` not needed (frames are written as they decode).
- Produces: `sealed class Host { Host(IConsumerSource consumer, IPhoneConnector connector, IDecoderFactory decoders, IFrameSink sink, Func<long> qpcNow, ILogSink log); Task RunAsync(CancellationToken); HostStatus Status { get; } }` with small interfaces wrapping the real types (so tests use fakes). Rules:
  1. Poll `consumer` every 100 ms. When active (w,h) and no phone session: connect (usbmux list → first USB device → `ConnectAsync(id, 47820)`), retry every 1 s while active; when connected → `StartAsync(w,h)`.
  2. When the requested size changes while streaming → `StopAsync`, new decoder at the new size, `StartAsync(new)`; drop any decoded frame whose size ≠ current request.
  3. When the consumer goes inactive → `StopAsync` within 1 s and close the session after 10 s idle (keep it warm for quick restarts).
  4. Each decoded frame → `sink.WriteFrame(nv12, w, h, stride, qpcNow())` immediately.
  5. `KeyframeNeeded` → `RequestKeyframeAsync`.
  6. On `Disconnected` → dispose decoder, back to rule 1 (card shows automatically via the DLL).
  7. `HostStatus`: link (`usb`/`none`), phone device name, consumer w×h, fpsIn/fpsOut (1 s window), capture→write latency p50/p95 ms (via `Clock.ToPcUs(captureUs)`), RTT, bitrate, thermal, battery.
- Console `Program.cs`: one status line per second, e.g. `link=usb phone=iPhone consumer=1920x1080 in=30.0 out=30.0 lat=48/71ms rtt=3ms 8.0Mbps thermal=nominal batt=82%+`; Ctrl+C clean stop (sends `stop`).

- [ ] **Step 1: Failing tests** (fakes): `Host_StartsPhoneWhenConsumerActive`, `Host_StopsPhoneWithin1sWhenInactive`, `Host_RestartsPhoneOnSizeChange` (no wrong-size write reaches the sink), `Host_RequestsKeyframeWhenDecoderAsks`, `Host_ReconnectsAfterDetach`, `Host_StampsFramesWithQpcAtWrite`, `Host_LatencyUsesClockOffset`.
- [ ] **Step 2:** FAIL → **Step 3:** implement → **Step 4:** 7 PASS; `windows/build.ps1 -Test` all green.
- [ ] **Step 5: Commit** `feat(host): raaicam-host wiring phone video into the virtual camera`.

---

### Task 8: Device bring-up, end-to-end checks, owner checklist

**Files:**
- Create: `docs/checklists/phase-1b.md`, `windows/tools/run-phone-checks.ps1`
- Modify: whatever small fixes the device run requires (each fix gets its own commit and is reported)

**Interfaces:**
- Consumes: CI artifact `RaaiCam-ipa` (T2/T3), `raaicam-host` (T7), `vcam-probe` (1A).

- [ ] **Step 1: Owner installs the app**: download the latest artifact to `spikes/out/RaaiCam.ipa`; owner deletes the old "RaaiCam Spike" app, then iloader → Import IPA → `RaaiCam.ipa`; opens RaaiCam, allows camera; screen shows Ready.
- [ ] **Step 2: Implement `run-phone-checks.ps1`** (phone plugged in, RaaiCam open, `raaicam-host` started by the script, all output to `windows/out/phone-checks/` gitignored):
  - **P1** probe 1920×1080, 300 frames, with a new probe flag `--expect camera`: a frame counts as **camera** when it is neither the card nor the test pattern (luma variance above a threshold set from the card's variance). Require ≥ 270 camera frames (the first ~1 s may be card while the phone starts) and fps 28–32.
  - **P2** probe 1280×720: same, and the host log shows a restart at 1280×720.
  - **P3** latency: from the host status lines during P1, p95 capture→write ≤ 100 ms.
  - **P4** stop: probe closes → host log shows `stop` sent within 1 s; phone telemetry `streaming=false` within 3 s.
  - **P5** 10-minute soak at 1080p: no host exceptions, fps 28–32 throughout, phone thermal recorded (pass if not `critical`).
- [ ] **Step 3: Run it and fix** what fails (stop and report if the fix is in a reviewed earlier task and not obvious).
- [ ] **Step 4: Write `docs/checklists/phase-1b.md`** (owner, small steps, pass/fail column): 1 Zoom/Meet/Discord show the live phone video, upright, sharp; 2 phone screen: Ready → Streaming (dark, red dot) when a meeting opens, back to Ready when it closes; 3 tap → Pause camera → meeting shows the card; unpause → video back; switch lens → wider view; 4 press the phone's side button (lock) mid-meeting → card within 1 s → unlock + open RaaiCam → video back within 3 s; 5 unplug USB → card; replug → video back within 3 s; 6 30-minute meeting: phone temperature status, any stutter.
- [ ] **Step 5: Commit** `test(e2e): phone end-to-end checks and owner checklist`.
- [ ] **Step 6: Owner gate** — owner runs the checklist; results recorded in the file and committed.

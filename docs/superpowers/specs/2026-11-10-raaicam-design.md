# RaaiCam — Product & Technical Design Spec

- **Date:** 2026-11-10
- **Status:** Draft for owner review
- **Owner:** project owner (product decisions) · Claude (design & implementation)
- **Related:** [Competitor & tech research](../../research/2026-11-10-competitor-and-tech-research.md)

---

## 1. Vision & positioning

**RaaiCam turns an iPhone into a smart, studio-quality webcam for Windows: free, fully local, and with no setup headaches.**

Apple's Continuity Camera (Center Stage, Portrait, Desk View) only works on Mac. NVIDIA Broadcast needs an RTX GPU. DroidCam and Iriun are basic and often painful to set up. RaaiCam gives Windows users with an iPhone and a non-RTX GPU:

1. **Zero-friction connection:** QR pairing once, then automatic USB↔Wi-Fi switching. The camera starts when a meeting app opens it.
2. **Intelligence on the PC GPU:** calm cinematic auto-framing and clean background blur on a GTX-class card.
3. **Full camera control:** every parameter can be set to Auto or Manual, all from the PC.
4. **Robustness:** clear placeholder cards instead of black or frozen frames, a thermal guard, adaptive quality, and a setup doctor.

### Goals
- G1: Daily-driver webcam for the owner's remote-work meetings.
- G2: Quality and latency noticeably better than DroidCam/Iriun free tiers.
- G3: **$0 forever:** no paid services, accounts, servers or Apple developer fee.
- G4: Developed and maintained entirely from Windows (no Mac).

### Non-goals (for now)
- App Store distribution or monetization.
- macOS/Linux desktop clients, Windows 10.
- Multiple phones at once, phone microphone, recording.
- Background image/video replacement (blur only).
- Cloud features of any kind.

---

## 2. Target user & context

| Item | Value |
|---|---|
| Primary user | Remote worker (the owner) in Zoom, Google Meet (Chrome/Edge), Discord, Slack. Teams is not required but should work. |
| Phone | iPhone 11 or 12 (no LiDAR, Lightning, A13/A14) on a tripod/desk stand, rear cameras facing the user, plugged in |
| PC | Windows 11, NVIDIA GTX 1660 Super (6 GB, no Tensor cores) |
| Network | Home Wi-Fi + USB cable |
| Skills | Owner is non-technical; Claude writes all code. Owner does on-device tests via checklists. |

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| D1 | Windows 11 first and only | Biggest unmet need; `MFCreateVirtualCamera` is Win11-only and works in browsers |
| D2 | AI runs on the PC, phone stays thin | Phone app can't be debugged without a Mac; iPhone 11/12 thermals; GPU available |
| D3 | Native Swift iOS app, built on GitHub Actions macOS runners (public repo) | Full camera API access; free unlimited cloud builds |
| D4 | Install via free Apple ID. **Phase 0 result: use iloader "Import IPA" from the PC, re-install every 7 days (D20).** SideStore on-device refresh is a later goal | $0, no paid account; SideStore sign-in is currently broken upstream |
| D5 | Windows stack: C# (.NET 10) + WinUI 3 tray app; C++ MF virtual-camera media source; ONNX Runtime (DirectML) for AI; D3D11 GPU pipeline | Native Win11 look, best performance and compatibility |
| D6 | Transport: VideoToolbox H.264 (HEVC optional) → USB via Apple usbmux (127.0.0.1:27015) or Wi-Fi | Hardware encode/decode, low bandwidth, <100 ms |
| D7 | 100% local; no accounts, telemetry or cloud | Privacy + $0 |
| D8 | Output 1080p30, adaptive down to 720p24 | What meeting apps use; GPU headroom |
| D9 | Tray app + compact panel (layout A: quick toggles), dark/light follows Windows, Mica | Approved mockup |
| D10 | Auto-framing: calm/cinematic, locked on main person | Owner preference |
| D11 | Phone UI: dimmed minimal + a few quick controls on tap | Battery/heat |
| D12 | QR pairing; Bonjour discovery for reconnects | Simple, secure |
| D13 | Connection loss → blurred last frame + "Reconnecting…" | Professional look |
| D14 | Windows app autostarts in the tray | Always ready |
| D15 | Phase 0 feasibility spike before real build | Zoom + MF virtual camera compatibility is unconfirmed |
| D16 | Eye contact = v3 experimental | No RTX; open-source models are lower quality |
| D17 | Global\ + an explicit DACL works; the spike DACL is NOT the production DACL. The spike used `D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;IU)`, which gives every interactive user full control. Phase 1 must grant only SYSTEM, LocalService and the logged-on user's SID, with GR|GW rights rather than GA. Created by the DLL inside Frame Server. | Phase 0: confirmed working; user-mode writer opens both by name |
| D18 | No DirectShow fallback in v1 (FR-VC-5 dropped) | Phase 0: Chrome, Edge, Zoom, Google Meet and Discord all show the MF virtual camera |
| D19 | iOS build pipeline: GitHub Actions `macos-15` + XcodeGen, unsigned .ipa | Phase 0: first CI run was green; later runs rebuilt for code changes (incl. the NWListener→BSD fix) |
| D20 | Install route: iloader (Windows) "Import IPA" with a free Apple ID, re-install from the PC every 7 days. SideStore PC-free refresh deferred | SideStore on-device sign-in fails with -80045 (upstream issues #1709, #1652); PC-free refresh not verified |
| D21 | USB tunnel: Apple Devices app provides usbmuxd on 127.0.0.1:27015; app port 47820 reachable over it | Phase 0: 1080p JPEG fetched in 66 ms (connect + fetch) on iPhone 11 family, iOS 26.6.2 |
| D22 | The iOS listener is a POSIX/BSD socket bound to INADDR_ANY, **not** `NWListener`. Any listener reachable over Wi-Fi must authenticate (pairing key) before sending video. A loopback-only USB listener is an option, but it is untested. | Phase 0: usbmux `Connect` to an `NWListener` port returns Result 3 (refused), Wi-Fi works; BSD socket works on both |

---

## 4. Scope by release

### v1 (Phases 1–2): "Smart webcam"
- Streaming phone→PC over USB and Wi-Fi with automatic failover
- QR pairing, encrypted link, auto-reconnect
- "RaaiCam" virtual camera visible in Zoom, Meet, Discord, Slack (Slack untested in Phase 0)
- Auto start/stop when an app opens or releases the camera
- Tray app + panel (preview, toggles, live stats)
- Auto-framing (calm, locked on main person, framing size options)
- Background blur (adjustable strength)
- Full manual camera controls (§5.4)
- Global hotkeys
- Setup doctor + first-run wizard
- Placeholder cards (waking / reconnecting / open the app / phone missing)
- Thermal guard + adaptive quality
- Phone screens: pairing, ready, streaming, warnings, quick controls

### v2 (Phase 3)
- Lighting & skin touch-up (auto face exposure, low-light relight, skin smoothing)
- Presets: manual + auto-apply per meeting app
- Overlays: name/title tag, privacy freeze, "Be right back" card
- Desk view: hotkey toggle with picture-in-picture face bubble (ultra-wide + perspective correction)
- Signing-certificate expiry warning

### v3 (Phase 4, lab)
- Eye-contact/gaze correction (experimental toggle)

---

## 5. Functional requirements

IDs: `FR-<area>-<n>`. **[v1]/[v2]/[v3]** = release.

### 5.1 Connection & pairing
- **FR-CON-1 [v1]** The PC app shows a QR code with: PC id, PC name, LAN IPs, port, one-time pairing token (valid 5 min) and the PC's public key.
- **FR-CON-2 [v1]** The phone scans the QR and completes a key exchange (X25519). Both sides store the long-term pairing (phone: Keychain; PC: DPAPI-protected file). One phone paired at a time; re-pairing replaces the old one.
- **FR-CON-3 [v1]** USB: the PC app detects the iPhone through Apple Mobile Device Service (usbmux on `127.0.0.1:27015`) and connects to the phone app's listening port over the USB tunnel.
- **FR-CON-4 [v1]** Wi-Fi: the phone advertises `_raaicam._tcp` via Bonjour; the PC also remembers the last IP. Connection is authenticated with the pairing key; all Wi-Fi traffic is encrypted (AES-256-GCM session keys).
- **FR-CON-5 [v1]** If both paths exist, prefer USB. Switch paths in under 1 s with no visible interruption (the new path requests a keyframe; the old path drains).
- **FR-CON-6 [v1]** On full disconnect, retry with backoff (0.5 s → max 5 s) indefinitely while the PC app runs.
- **FR-CON-7 [v1]** Pairing can be reset from either side ("Unpair").

### 5.2 Streaming
- **FR-STR-1 [v1]** Phone captures from the rear wide (1×) or ultra-wide (0.5×) camera.
- **FR-STR-2 [v1]** Capture at 1920×1080 @ 30 fps (wide lens). When auto-framing needs more room, capture 4K and let the PC crop (enabled only if thermals allow; see FR-PWR).
- **FR-STR-3 [v1]** Hardware H.264 encode (VideoToolbox), real-time mode, no B-frames, keyframe every 2 s and on request, target bitrate 8 Mbps USB / 4–8 Mbps adaptive Wi-Fi. HEVC is an optional setting.
- **FR-STR-4 [v1]** Adaptive quality: the PC measures loss/jitter/latency on Wi-Fi and asks the phone to change bitrate and then resolution (1080p30 → 720p30 → 720p24).
- **FR-STR-5 [v1]** Glass-to-glass latency target: ≤100 ms USB, ≤150 ms Wi-Fi (p95).
- **FR-STR-6 [v1]** The phone camera runs only while some app is consuming the RaaiCam virtual camera (or the preview panel is open). Otherwise the phone is idle (camera session stopped).

### 5.3 Virtual camera
- **FR-VC-1 [v1]** Register a Windows 11 virtual camera named **"RaaiCam"** via `MFCreateVirtualCamera` (camera add/remove needs no admin, but the one-time COM registration of the DLL needs an elevated HKLM `regsvr32`; see §9.3), exposing NV12 1920×1080@30 and 1280×720@30.
- **FR-VC-2 [v1]** The C++ media source (runs inside Windows Frame Server) reads frames from the RaaiCam app through a shared-memory ring buffer + named events. It never runs AI or networking itself.
- **FR-VC-3 [v1]** When the RaaiCam app or phone isn't delivering frames, the media source/app outputs placeholder cards (FR-UI-12) rather than black.
- **FR-VC-4 [v1]** Detect "camera in use / released" from the media source and signal the app (drives FR-STR-6).
- **FR-VC-5 [dropped]** DirectShow fallback. Phase 0 showed Chrome, Edge, Zoom, Google Meet and Discord all see the MF virtual camera (D18). Revisit only if a target app fails later.
- **FR-VC-6 [v1]** Report which app is using the camera (process name) to the app, when Windows exposes it. Used for "IN USE · Zoom" and v2 per-app presets.

### 5.4 Camera controls (all from PC; each has Auto/Manual)
- **FR-CAM-1 [v1]** Lens: wide / ultra-wide. Mirror on/off (default on). The setting applies to both the preview and the meeting output; most meeting apps also mirror self-view on their own side.
- **FR-CAM-2 [v1]** Zoom (digital/optical): Auto (owned by auto-framing) or manual slider.
- **FR-CAM-3 [v1]** Focus: Auto (continuous, biased to the detected face via the phone's face metadata), tap-to-focus on the PC preview, manual lens position.
- **FR-CAM-4 [v1]** Exposure: Auto (face-weighted), EV compensation, lock; manual ISO and shutter.
- **FR-CAM-5 [v1]** White balance: Auto or manual Kelvin (2500–8000 K) + tint.
- **FR-CAM-6 [v1]** Look (applied on PC GPU): brightness, contrast, saturation, warmth, sharpness, LUT filter (.cube import + a few built-ins).
- **FR-CAM-7 [v1]** "Reset all to Auto" button.
- **FR-CAM-8 [v1]** The phone reports its capabilities (ISO/shutter ranges, lenses); the UI shows only supported ranges.

### 5.5 Auto-framing
- **FR-AF-1 [v1]** Detect faces on the PC (lightweight ONNX face detector), track the **main person**: the person present at session start or the largest/most central face, kept frame to frame by box overlap (IoU) and motion continuity. Face-recognition embeddings are out of scope for v1.
- **FR-AF-2 [v1]** Other people entering do not change the framing (locked on main person). If the main person leaves for >3 s, slowly ease out to the wide default; re-acquire when a face returns.
- **FR-AF-3 [v1]** Calm motion: dead-zone (no movement until the face leaves the inner 60% target box or size changes >15%), critically damped smoothing, max pan speed limit; no jitter at rest.
- **FR-AF-4 [v1]** Framing sizes: Head & shoulders (default) / Waist up / Wide. Respect headroom (eyes at ~1/3 from top).
- **FR-AF-5 [v1]** Never upscale beyond 1.5× of source pixels; if more zoom is needed, request 4K capture (if thermals allow) or switch lens.
- **FR-AF-6 [v1]** Toggle via panel/hotkey; when turned off, ease back to the manual zoom setting over 0.5 s.

### 5.6 Background blur
- **FR-BG-1 [v1]** Person segmentation on PC GPU via ONNX Runtime (DirectML). Baseline model: MediaPipe Selfie Segmentation; candidate upgrade: Robust Video Matting (chosen by benchmark in Phase 2).
- **FR-BG-2 [v1]** Full-resolution edge refinement (guided filter) + temporal smoothing to prevent flicker/halos.
- **FR-BG-3 [v1]** Blur strength slider (light → heavy); blur computed on GPU (separable Gaussian or lens-like).
- **FR-BG-4 [v1]** The blur pipeline must keep 30 fps at 1080p on a GTX 1660 Super with auto-framing on (budget: ≤12 ms GPU per frame for all AI + compositing).

### 5.7 Lighting & touch-up [v2]
- **FR-LT-1** Face-weighted auto exposure (phone side, uses face metadata) as default; "Low-light boost" (PC-side denoise + tone curve).
- **FR-LT-2** Skin smoothing (strength slider) using a face-region bilateral/frequency-separation filter on GPU.
- **FR-LT-3** "Studio light" style relight (brighten face, slightly darken background) using the segmentation mask.

### 5.8 Presets [v2]
- **FR-PR-1** A preset stores: lens, all camera controls, look, framing on/size, blur on/strength, touch-up, overlays.
- **FR-PR-2** Built-in presets: Work, Casual, Present. Users can create, rename, delete, duplicate.
- **FR-PR-3** Auto-apply: map a preset to an app (by process name: Zoom.exe, chrome.exe/msedge.exe, Discord.exe, slack.exe). Applied when that app starts using the camera.
- **FR-PR-4** Switch by tray pill, tray right-click menu, or hotkey (Ctrl+Alt+1..9).

### 5.9 Overlays [v2]
- **FR-OV-1** Name/title lower-third (text, style, position, auto-hide after N seconds option).
- **FR-OV-2** Privacy freeze: hotkey freezes the current output frame.
- **FR-OV-3** "Be right back" card (blurred background + text), hotkey toggle.

### 5.10 Desk view [v2]
- **FR-DV-1** Hotkey/panel toggle switches output to a desk view from the ultra-wide lens with perspective correction (user sets the 4 corners once in a calibration screen).
- **FR-DV-2** The user's face appears as a circular PiP bubble (cropped from the same ultra-wide frame; the iPhone 11/12 can't stream two lenses at once reliably).
- **FR-DV-3** Smooth crossfade (300 ms) between normal and desk view.

### 5.11 Eye contact [v3, experimental]
- **FR-EC-1** Experimental toggle in "Lab" section; evaluate open-source gaze redirection models; ship only if it holds 30 fps and looks natural.

### 5.12 Power & thermals
- **FR-PWR-1 [v1]** Phone monitors `ProcessInfo.thermalState`, battery level and charging state; reports to PC every 2 s.
- **FR-PWR-2 [v1]** Thermal guard: on `.serious` drop 4K→1080p capture and 1080p30→720p30; on `.critical` 720p24 + warning; recover after 2 min at `.nominal/.fair`.
- **FR-PWR-3 [v1]** Phone screen: dims to minimum after 10 s; idle timer disabled while the app is open (screen never locks by itself while the app is open).
- **FR-PWR-4 [v1]** Warning if the phone is not charging during a session and battery < 30%.

### 5.13 Hotkeys [v1]
Defaults (all rebindable, conflict detection):
| Action | Default |
|---|---|
| Toggle auto-framing | Ctrl+Alt+F |
| Toggle blur | Ctrl+Alt+B |
| Privacy freeze [v2] | Ctrl+Alt+P |
| Be right back [v2] | Ctrl+Alt+R |
| Desk view [v2] | Ctrl+Alt+D |
| Presets 1–9 [v2] | Ctrl+Alt+1..9 |
| Open panel | Ctrl+Alt+C |

### 5.14 Diagnostics & setup doctor [v1]
- **FR-DX-1** Checks: Windows 11 build ≥ 22000; Apple Mobile Device Service installed & running; Windows camera privacy ("Let desktop apps access your camera") enabled; firewall rule for RaaiCam (private networks); phone and PC on same subnet (when Wi-Fi only); GPU/DirectML available; virtual camera registered.
- **FR-DX-2** Each failed check shows a plain-language explanation and a fix button or link.
- **FR-DX-3** Live stats in panel: latency, fps (in/out), bitrate, connection type, phone temperature/thermal state, battery.
- **FR-DX-4** Remote logging: the phone app streams its logs to the PC app; logs from both sides saved to `%LOCALAPPDATA%\RaaiCam\logs` (rolling, 7 days). "Export diagnostics" button zips logs + settings (no video).

### 5.15 Certificate expiry [v2]
- **FR-CERT-1** The phone app reads its provisioning expiry date and reports it; the PC panel warns 2 days before expiry with a "how to refresh" guide.

---

## 6. User journeys

### J1: First setup (guided wizard)
1. Install RaaiCam for Windows → wizard opens → setup doctor runs (FR-DX-1) and guides fixes (e.g., install Apple Devices app from Microsoft Store).
2. "Install the iPhone app" step: illustrated guide for iloader (one-time) and installing the RaaiCam .ipa (downloaded from the project's GitHub Releases).
3. Phone app opens → camera permission → scan QR on PC → paired.
4. Calibrate: preview appears; RaaiCam suggests lens/framing size; user confirms. Done screen: "Open Zoom and pick 'RaaiCam' as your camera."

### J2: Daily meeting
1. PC boots → RaaiCam starts in tray (FR autostart). Phone sits open on stand, dimmed, "Ready".
2. User joins Zoom → Zoom opens "RaaiCam" → PC signals phone → camera starts; meeting shows "Waking camera…" card → fades into live video (target ≤1.5 s).
3. During meeting: hotkeys / tray panel for blur, framing, freeze.
4. Meeting ends → camera released → phone returns to Ready within 3 s.

### J3: Cable unplugged mid-meeting
USB lost → Wi-Fi path already warm (keep-alive) → switch < 1 s → stats show "Wi-Fi". Plug back → returns to USB.

### J4: Phone app closed / phone missing
Virtual camera shows "Open RaaiCam on your iPhone" card; Windows toast notification (once per session).

### J5: Phone overheating
Thermal guard steps quality down (FR-PWR-2); panel and phone show a warning with tips.

---

## 7. Information architecture & screens

### Windows
1. **Tray icon:** states: idle (grey), connected (white), in use (red dot), warning (amber). Left-click → panel. Right-click → menu: Presets ▸, Toggle blur, Toggle framing, Settings, Setup doctor, Quit.
2. **Tray panel (layout A, ~380×600):** header (name, connection), live preview with framing outline and "IN USE · App" badge, preset pills [v2], 4 toggle tiles (Auto-frame, Blur, Touch-up [v2], Desk view [v2]), blur slider, "Camera settings ›", stats row.
3. **Camera settings (panel sub-page):** Lens & view / Focus & exposure / Look; AUTO switch per control; Reset all; Save to preset [v2]; click preview to focus.
4. **Settings window:** tabs: General (autostart, theme, language, output resolution), Presets [v2], Hotkeys, Connection (paired phone, unpair, USB/Wi-Fi preferences, codec), About (version, logs, export diagnostics).
5. **Setup wizard + setup doctor.**
6. **Desk view calibration [v2].**
7. **Virtual-camera placeholder cards:** Waking camera · Reconnecting (blurred last frame) · Open RaaiCam on your iPhone · Be right back [v2] · Frozen [v2].

### iPhone
1. **Pairing:** camera permission + QR scanner (+ discovered PC hint).
2. **Ready (idle):** big dim icon, connection & battery/temperature pills.
3. **Streaming:** near-black screen, tiny red dot + "Live · App · temp"; tap → quick controls.
4. **Quick controls sheet:** Pause camera (privacy), switch lens, unpair, show diagnostics.
5. **Warnings:** heat, connection lost, certificate expiring.

Approved mockups are kept in `.superpowers/brainstorm/` (local only).

---

## 8. UI/UX direction
- Native Windows 11 look: WinUI 3, Mica backdrop, Segoe UI Variable, rounded corners, follows system light/dark (dark default in mockups). Accent: indigo `#5B7CFF`; live/red `#E5484D`; warning amber `#FFB224`; OK green `#3DDC84`.
- Principles: **one click for the things you do in meetings**; everything else one level deeper. Every manual control can return to Auto. No modal dialogs during meetings; use tray notifications.
- Copy: plain language, no jargon in primary UI ("Camera is warm" not "thermalState serious"); technical details in stats/diagnostics.
- Accessibility: keyboard navigable panel, screen-reader names on all controls, contrast ≥ 4.5:1.
- iPhone: black/near-black UI to minimize light and heat; large text; SF Symbols.

---

## 9. Technical architecture

### 9.1 Components

```
D:\RaaiCam
├─ ios/                      Swift iOS app (SwiftUI + AVFoundation + VideoToolbox + Network.framework)
├─ windows/
│  ├─ RaaiCam.App/           C# WinUI 3 tray app (UI, settings, presets, hotkeys, setup doctor)
│  ├─ RaaiCam.Core/          C# engine: connection manager, usbmux client, protocol, session control
│  ├─ RaaiCam.Media/         C# GPU pipeline: MF decode → D3D11 textures → AI → compositing → NV12 out
│  ├─ RaaiCam.Vision/        C# ONNX Runtime (DirectML): face detection, segmentation, framing controller
│  ├─ RaaiCam.VirtualCam/    C++ MF media source DLL + registration (shared-memory frame reader)
│  └─ tests/                 unit + pipeline tests (recorded clips)
├─ protocol/                 protocol spec + shared test vectors
├─ spikes/                   Phase 0 throwaway code
├─ docs/
└─ .github/workflows/        iOS build (macOS runner) + Windows build/test
```

### 9.2 iOS app (thin)
- **Capture:** `AVCaptureSession` with `AVCaptureDevice` for `.builtInWideAngleCamera` / `.builtInUltraWideCamera` (back). `AVCaptureMetadataOutput` face metadata used *on phone* to drive focus/exposure point of interest (cheap, no ML code).
- **Encode:** `VTCompressionSession` H.264 High, `kVTCompressionPropertyKey_RealTime`, `AllowFrameReordering=false`, average bitrate + data-rate limits, keyframe interval 60.
- **Network:** a POSIX/BSD socket listener bound to INADDR_ANY on a fixed port (47820) for USB/Wi-Fi TCP control+video. `NWListener` does **not** work: usbmux connections to it are refused (Phase 0, D22). Bonjour advertisement `_raaicam._tcp` (mechanism to be chosen in Phase 1; not tested in Phase 0); Wi-Fi video over UDP (`NWConnection`) with sequence numbers.
- **Control handler:** applies camera commands (lens, zoom, focus, exposure, ISO, shutter, WB), start/stop capture, keyframe/bitrate/resolution requests.
- **Telemetry:** thermal state, battery, charge state, fps, encoder bitrate, provisioning expiry [v2]; log forwarding.
- **UI:** SwiftUI screens per §7; `UIApplication.isIdleTimerDisabled = true`; brightness dimming.
- **Min iOS:** 17.0 (iPhone 11/12 support newer). No third-party dependencies.

### 9.3 Windows app
- **Runtime:** .NET 10, WinUI 3 (Windows App SDK), unpackaged with an installer (Inno Setup or MSIX sideload) that installs the virtual-camera DLL only to an admin-only-writable location (e.g. `%ProgramFiles%\RaaiCam`), never a user-writable folder, because Frame Server (LocalService) loads it, and registers it. Registration is a one-time elevated step: HKCU-only COM registration fails (`IMFVirtualCamera::Start` returns 0x80070003), so the installer runs `regsvr32` into HKLM. Frame Server keeps the DLL loaded for about 35-50 s after use, so updates must wait for it or restart the service.
- **Tray:** H.NotifyIcon.WinUI (or equivalent); single-instance; autostart via Startup task/registry Run key.
- **Connection manager (RaaiCam.Core):**
  - `UsbmuxClient`: speaks usbmux plist protocol over TCP 127.0.0.1:27015 (`Listen` for attach/detach, `Connect` to device port 47820).
  - `WifiDiscovery`: mDNS browse for `_raaicam._tcp` + last-known IP.
  - `Link` abstraction with two implementations (USB-TCP, Wi-Fi TCP control + UDP video); `LinkSelector` prefers USB, keeps Wi-Fi warm with keep-alives for instant failover.
  - `Session`: pairing/auth, crypto (X25519 + HKDF + AES-256-GCM via .NET `System.Security.Cryptography`), command/telemetry dispatch.
- **Media pipeline (RaaiCam.Media):** Media Foundation H.264 decoder with D3D11 device manager (NVDEC on NVIDIA) → NV12 `ID3D11Texture2D` → convert to RGB → Vision passes → compositing compute shaders (crop/scale with Lanczos/bicubic, blur, look/LUT, overlays) → NV12 → write to shared memory for the virtual camera and to the panel preview (SwapChainPanel). C# D3D11 interop via Vortice.Windows.
- **Vision (RaaiCam.Vision):** ONNX Runtime + DirectML EP sharing the D3D12/DX device where possible (fallback: CPU copy at low res). Face detector at ~320 px every frame (or every 2nd frame + tracking); segmentation at 256–512 px; framing controller (pure C#, unit-tested).
- **Virtual camera (RaaiCam.VirtualCam, C++):** `IMFMediaSource` + stream implementation per Microsoft VCamSample pattern; registration via `MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_System, MFVirtualCameraAccess_CurrentUser, ...)`. Reads frames from a named shared-memory ring buffer (`Global\` naming with an explicit DACL, confirmed in Phase 0, D17; the production DACL grants only SYSTEM, LocalService and the logged-on user's SID, with GR|GW rather than GA) with a header (frame counter, timestamp, width, height, format, state). Writes "consumer active + client process" back to the header. Phase 0 found: a single unlocked buffer tears, so use double buffering or a sequence lock; pace output with timer-driven delivery rather than sleeping in `RequestSample`; a second simultaneous consumer gets a clean `MF_E_HW_MFT_FAILED_START_STREAMING`.

### 9.4 Protocol (v1)
Defined in `protocol/PROTOCOL.md`, versioned (`proto=1`).
- **Framing (TCP):** `[u8 type][u32 length][payload]`. Types: `HELLO`, `AUTH`, `CMD` (JSON), `TELEMETRY` (JSON), `LOG` (text), `VIDEO` (H.264 Annex-B access unit + `u64` capture timestamp µs + `u32` frame seq), `PING/PONG`.
- **Wi-Fi video (UDP):** encrypted packets `[seq][frame seq][frag idx/count][ts]` + payload; PC requests keyframe on loss (`CMD keyframe`). No retransmission (latency first).
- **Clock:** PING/PONG every 1 s to estimate offset → latency stats.
- **Commands (JSON):** `start {res, fps, lens, codec, bitrate}`, `stop`, `set {focus|exposure|iso|shutter|wb|zoom|lens|...}`, `keyframe`, `bitrate {kbps}`, `capabilities?`, `unpair`.
- **Telemetry (JSON):** `{thermal, battery, charging, fpsCapture, fpsEncode, bitrate, lens, iso, shutter, wbK, focusPos, certExpiry}`.

### 9.5 Data & storage (no database, no server)
| Data | Location | Format |
|---|---|---|
| App settings | `%APPDATA%\RaaiCam\settings.json` | JSON (versioned schema) |
| Presets [v2] | `%APPDATA%\RaaiCam\presets.json` | JSON |
| Pairing keys (PC) | `%APPDATA%\RaaiCam\pairing.bin` | DPAPI-encrypted |
| Logs | `%LOCALAPPDATA%\RaaiCam\logs\` | rolling text, 7 days |
| AI models | `<install dir>\models\` | ONNX |
| LUTs | `%APPDATA%\RaaiCam\luts\` | .cube |
| Pairing keys (phone) | iOS Keychain | — |
| Phone settings | `UserDefaults` | — |

No APIs to external services. The only network traffic is phone↔PC on the LAN/USB.

### 9.6 Build & distribution ($0)
- **Repo:** public GitHub repository (owner's connected account). Nothing is pushed without owner approval.
- **iOS CI:** GitHub Actions `macos-15` + XcodeGen (D19) → `xcodebuild build` with code signing disabled → hand-zip the `.app` into a `Payload/` folder as an unsigned `.ipa` → upload as workflow artifact + GitHub Release asset.
- **Install (D20):** iloader on Windows signs in with a free Apple ID and installs via "Import IPA"; re-install from the PC every 7 days. SideStore's on-device sign-in currently fails with -80045 (upstream issues #1709, #1652), so PC-free refresh is unverified; revisit when upstream is fixed.
- **Windows CI:** GitHub Actions `windows-latest` → build, unit tests, pipeline tests, installer artifact.
- **Windows install:** installer from GitHub Releases (unsigned; SmartScreen "More info → Run anyway" documented in the wizard guide).

---

## 10. AI & computer vision requirements

| Capability | Model (initial) | Input | Budget on GTX 1660S | Quality bar |
|---|---|---|---|---|
| Face detection | YuNet or SCRFD-500M (ONNX) | 320×180–640×360 | ≤2 ms | Detect frontal/three-quarter faces ≥ 60 px wide; stable boxes |
| Framing controller | Custom (dead-zone + critically damped spring + speed cap) | Face boxes | <0.1 ms CPU | No visible jitter when seated still; pans feel slow and deliberate |
| Segmentation | MediaPipe Selfie Seg → candidate RVM (MobileNetV3) | 256×256 / 512×288 | ≤6 ms | No flicker at rest, hair edges acceptable, no background bleed on shoulders |
| Edge refine | Guided filter on GPU | full res | ≤2 ms | Smooth, halo-free |
| Touch-up [v2] | Face landmarks (lightweight) + GPU filters | full res | ≤2 ms | Natural, adjustable |
| Gaze [v3] | Open-source gaze redirection | eye crops | ≤4 ms (measured in Phase 4) | Natural or not shipped |

- Total AI + compositing GPU budget: **≤ 12 ms/frame** at 1080p30 (leaves headroom for decode/encode and other apps).
- Models are bundled (no download at runtime); licenses must be permissive (Apache/MIT/BSD) and recorded in `THIRD_PARTY_NOTICES`.
- Benchmark harness (Phase 2) runs each model on recorded clips and reports ms/frame + a visual contact sheet; model choice documented.

---

## 11. Non-functional requirements
- **Latency:** ≤100 ms USB, ≤150 ms Wi-Fi (p95), measured by stats.
- **Startup:** camera live in meeting ≤1.5 s after the app opens the virtual camera (phone in Ready state).
- **Failover:** USB↔Wi-Fi switch < 1 s.
- **Stability:** 4-hour session without crash, leak (memory growth < 50 MB) or drift.
- **PC resources:** ≤ 10% CPU (Ryzen/Intel 6-core class) and ≤ 12 ms GPU per frame with blur + framing.
- **Phone:** sustains 1080p30 for 2 h plugged in at room temperature without hitting `.critical` (iPhone 11/12, case off).
- **Privacy/security:** no outbound internet traffic from either app (except the user opening docs links); Wi-Fi traffic encrypted and authenticated; pairing token single-use, 5-minute validity.
- **Compatibility:** Zoom (desktop), Google Meet in Chrome and Edge, Discord, Slack (Slack untested in Phase 0); Teams best-effort.

---

## 12. Testing strategy
1. **Unit tests (C#, xUnit):** protocol framing/parsing, crypto handshake (test vectors), usbmux message encoding, framing controller (scripted face trajectories → assert smoothness/no jitter), adaptive-quality state machine, thermal step-down, settings/preset serialization.
2. **Pipeline tests:** recorded H.264 clips (various lighting, movement, two people) fed through decode → vision → composite; assert fps budget and dump frames for visual review.
3. **Synthetic source mode:** Windows app can run with a test pattern or a looping clip instead of a phone; lets the whole PC side (virtual camera, UI, effects) be tested without the iPhone.
4. **iOS:** unit tests for command handling and encoder config run on the CI simulator; camera/encode verified on device by owner checklist.
5. **Manual acceptance checklists per phase** (owner): written in `docs/checklists/phase-N.md`, each item pass/fail with screenshots if failed.
6. **Soak test:** 4-hour run with synthetic source + 2-hour run with phone.

---

## 13. Roadmap

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **0 · Spike (throwaway) — DONE 2026-11-10, see `docs/spikes/phase0-results.md`** | (a) C++ MF virtual camera with animated test pattern; (b) GitHub Actions builds a hello-world iOS .ipa; (c) installed on the owner's iPhone via iloader "Import IPA" (SideStore sign-in failed, -80045); (d) C# usbmux client connects to the phone app and receives one frame | Result: "RaaiCam Test" showed color bars in Chrome, Edge, Zoom, Google Meet and Discord (Slack not tested); app runs on phone; USB round-trip works (66 ms). All apps passed, so FR-VC-5 is dropped. |
| **1 · Basic webcam** | iOS capture/encode/stream/dim UI; pairing; USB+Wi-Fi failover; decode; virtual camera; basic tray panel with preview + stats | Owner uses it in a real meeting; latency targets met |
| **2 · Smart v1** | Auto-framing, blur, manual controls, hotkeys, setup doctor, wizard, placeholder cards, thermal guard, model benchmark | All v1 FRs pass checklist; 2-hour real meeting test |
| **3 · v2** | Touch-up, presets + per-app, overlays, desk view, cert expiry | v2 FRs pass |
| **4 · v3 lab** | Eye-contact experiment | Ship/no-ship decision |

---

## 14. Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| ~~Zoom (or another app) doesn't list MF virtual cameras~~ | Resolved | Phase 0: Chrome, Edge, Zoom, Meet and Discord all work; no DirectShow fallback (D18) |
| ~~Frame Server can't access shared memory / DLL path~~ | Resolved | Phase 0: `Global\` + DACL works (D17); needs a one-time elevated HKLM `regsvr32` (installer) |
| Virtual-camera tearing / pacing | Medium | Double buffer or seqlock; timer-driven delivery (Phase 1) |
| SideStore broken (-80045) / free-signing changes by Apple | Medium | iloader re-install every 7 days (D20); revisit SideStore later; keep phone app small; option to switch to paid account later |
| 7-day free-signing expiry needs a PC step | Medium | Phone app shows expiry [v2]; wizard explains the weekly re-install |
| Phone app behavior when the screen locks | Medium | Unverified in Phase 0; test early in Phase 1 (idle timer disabled, dimmed UI) |
| C: drive nearly full on the dev PC (about 2 GB free) | Low/Med | Tools live on D:; keep installs and build output off C: |
| Apple Mobile Device Service missing/broken | Medium | Setup doctor detects + guides install of Apple Devices app; Wi-Fi still works |
| iPhone 11/12 overheating at 4K | Medium | 4K only on demand; thermal guard; prefer 1080p + lens switch |
| GTX 1660S too slow for RVM | Medium | Benchmark; MediaPipe baseline; run segmentation every 2nd frame + temporal interpolation |
| No on-device debugging for iOS | Medium | Remote logging to PC; tiny iOS surface; CI simulator tests |
| Wi-Fi interference (latency spikes) | Low/Med | Adaptive bitrate, keyframe-on-loss, USB preferred |

---

## 15. Open items (to settle during implementation, not blockers)
- Exact face-detector model (YuNet vs SCRFD): benchmark in Phase 2.
- Segmentation model (MediaPipe vs RVM): benchmark in Phase 2.
- Installer technology (Inno Setup vs MSIX sideload): decide in Phase 1 based on virtual-camera DLL registration needs.
- ~~Repo name on GitHub~~: closed. Repo "RaaiCam" created and pushed in Phase 0 with owner consent.
- ~~Installer technology decision depends on DLL registration~~: narrowed. Registration needs an elevated HKLM `regsvr32`, so the installer must run elevated once (Inno Setup is the likely fit); decide in Phase 1.
- Phone app behavior after screen lock (not verified in Phase 0).
- SideStore PC-free refresh: revisit once upstream fixes -80045.
- Two real apps using the camera at once (only the automated probe was run).

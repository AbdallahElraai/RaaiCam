# Phase 0 — Feasibility spike results

## Virtual camera (Task 2)

Recorded 2026-11-10 on the owner's PC (Windows 11 build 26200). Verdict: **feasible**. A Media Foundation virtual camera (`MFCreateVirtualCamera`), whose media source DLL runs inside the Frame Server service, gets frames from a separate user-mode C# process through named shared memory, and falls back to a "no signal" pattern when the writer is absent or stops.

### What runs where

| Piece | Process / account / session |
|---|---|
| `RaaiCamVCam.dll` (media source, creates the mapping) | `svchost.exe -k Camera -s FrameServer`, `LOCAL SERVICE`, session 0 |
| `registrar.exe` (calls `MFCreateVirtualCamera`, Session lifetime) | user, session 1 (also loads the DLL briefly for validation) |
| `FrameWriter` (.NET 10, writes NV12) | user, session 1, medium integrity |
| `probe.exe` (headless capture check) | user, session 1 |

### Shared memory namespace and DACL

- `CreateFileMappingW("Global\RaaiCamSpikeFrames")` and `CreateEventW("Global\RaaiCamSpikeFrameReady")` **succeeded on the first try from inside Frame Server** (no Win32 error; the `Local\` fallback was never needed). LocalService holds `SeCreateGlobalPrivilege`.
- DACL from SDDL `D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;IU)` worked: the writer, as the interactive user, opened both objects by their `Global\` names with read/write access. No errors were seen.
- Note: in session 0, `Local\` and `Global\` resolve to the same namespace, so the fallback would also have been reachable from the user session as `Global\`.

### Registration

| Attempt | Result |
|---|---|
| HKCU only (`registrar.exe --register-dll`, no admin) | `MFCreateVirtualCamera` OK, **`IMFVirtualCamera::Start` → `0x80070003` (ERROR_PATH_NOT_FOUND)**. The DLL loaded in the registrar process, but Frame Server (LocalService) cannot see the user's HKCU CLSID. |
| HKLM via elevated `regsvr32 /s RaaiCamVCam.dll` (one UAC prompt) | `Start` OK. The camera appears as **"RaaiCam Test (Windows Virtual Camera)"**. Windows appends the suffix. |

So, like VCamSample, the COM registration needs **one elevated step** (HKLM). After that, registering and removing the camera (`MFVirtualCameraAccess_CurrentUser`, Session lifetime) needs no admin. Phase 1's installer must do the HKLM registration.

Frame Server keeps the DLL loaded for about 35–50 s after the last client and the camera are gone, which locks the file. Rebuilds must wait for that or restart the service.

### Automated Step 6 results (`spikes/vcam/run-checks.ps1`, final build)

| Check | Result |
|---|---|
| A. Writer absent, 1280×720 | NO_SIGNAL: mean Y 96.62 (0x60), 99.6 % of pixels = 0x60, white block moves 4 px/frame (x 0→116 over 30 frames), 30.0 fps, no crash |
| A2. Writer absent, 1920×1080 | NO_SIGNAL: mean Y 96.27, 30 fps |
| B. Writer running, then camera opened | LIVE from frame 1 (frame 0 is the pattern for 33 ms). The bar samples are exactly the writer's BT.601 values (180/162/131/112/84/65/35/16 Y), 60/60 frames distinct, writer printed `consumerActive=1` and then `0` |
| C. Camera open, writer started mid-stream | Writer opened the mapping at 16:22:29.196; the probe turned LIVE at 16:22:29.278 (**~80 ms**) |
| C. Writer killed (`Stop-Process -Force`) mid-stream | Killed at 16:22:33.142; the probe turned NO_SIGNAL at 16:22:33.683 (**~540 ms**, which is the 500 ms staleness window plus one frame). The stream never stopped, at 30 fps throughout |
| E. Stress: probe force-killed mid-stream 10× (0.45–1.8 s in), writer running | Afterwards a probe streams at 30 fps. Same FrameServer svchost PID before and after, 0 Application Error (1000) events, active-stream count back to 0 every time (fix round 1, after the locking fix) |
| D. Two consumers at once (two `IMFSourceReader`s) | First consumer: 120 frames LIVE, unaffected. Second consumer: `ReadSample` failed at once with `0xC00D3704` (`MF_E_HW_MFT_FAILED_START_STREAMING`, "in use"). This was a clean error, not a hang |

The probe's saved frames are `spikes/out/*.bmp` (gitignored). They were inspected visually: a grey frame with a white block, and color bars with a moving white bar in the bottom strip.

### Known spike limitations

- Single shared buffer with no lock: the moving bar can show **tearing** when the reader copies during a write (visible in the B capture). Phase 1 needs double buffering or a sequence lock.
- The source paces itself to 30 fps by sleeping in `RequestSample` (Frame Server otherwise requests samples back to back, about 2500/s were measured).
- CPU (system-memory) samples only. The DXGI manager is ignored.

### Build and run commands

See `.superpowers/sdd/2026-11-10-phase0-feasibility-spike/task-2-report.md` for the full list. In short:

```powershell
powershell -ExecutionPolicy Bypass -File spikes\vcam\build.ps1           # DLL, registrar, probe (NMake, Release)
dotnet build spikes\framewriter -c Release                                # FrameWriter.dll
# once, elevated (UAC): HKLM COM registration
Start-Process regsvr32 -ArgumentList '/s','"D:\RaaiCam\spikes\vcam\build\bin\RaaiCamVCam.dll"' -Verb RunAs -Wait
spikes\vcam\build\bin\registrar.exe                                       # keep open; Enter removes the camera
dotnet spikes\framewriter\bin\Release\net10.0-windows\FrameWriter.dll     # color bars; Ctrl+C stops
```

### Step 7 — owner app checks

Owner-run on 2026-11-10 with `registrar.exe` open and the writer running (color bars). A screenshot of the browser test page was shared in chat; the other results were reported by the owner.

| # | App | Writer running: color bars? | Notes |
|---|---|---|---|
| 1 | Zoom desktop | Yes | |
| 2 | Chrome | Yes | |
| 3 | Edge | Yes | |
| 4 | Discord | Yes | |
| 5 | Google Meet (Chrome) | Yes | |
| 6 | Two apps at once | Not checked | The automated probe (check D above) showed a clean `MF_E_HW_MFT_FAILED_START_STREAMING` for the second consumer |
| 7 | Writer-stopped pattern in real apps | Not reported | Verified only by the automated probe (check A) |

**Verdict:** every app the owner tried lists and shows the camera, so the DirectShow fallback (FR-VC-5) is **not needed**.

## iOS build pipeline (Task 3)

- GitHub Actions on `macos-15`. XcodeGen (`brew install xcodegen`, version not pinned) generates the project from `spikes/ios-hello/project.yml`. `xcodebuild` runs with signing disabled and the unsigned `RaaiCamSpike.ipa` is uploaded as a workflow artifact.
- The first CI run (42826602743) was green with no fixes. Later runs: 42828262331 (after a string fix) and 42849951146 (BSD-socket listener, the build now on the phone). Build minutes were not recorded; GitHub shows them per run.
- The app is a camera grabber plus a one-shot frame server: `[u32 BE length][JPEG]`, then close, on TCP 47820.

## Install route (Task 5)

| Step | Result |
|---|---|
| iloader (Windows) sign-in with a free Apple ID, SideStore install | OK |
| SideStore on-device Apple ID sign-in | **Fails with -80045.** Known upstream since about 2026-10-12 (https://github.com/SideStore/SideStore/issues/1709, https://github.com/SideStore/SideStore/issues/1652). The workaround was not completed |
| Install `RaaiCamSpike.ipa` via iloader "Import IPA" | OK. The app runs (camera running, listener ready) |
| PC-free refresh (SideStore) | **Not verified** |
| Behavior after the screen locks | **Not verified** |

## USB tunnel (Task 4)

Device: iPhone 11 family (A13), iOS 26.6.2. Apple Devices (Microsoft Store) provides usbmuxd on `127.0.0.1:27015` once it has been run once; before that nothing was listening.

| Probe | Result |
|---|---|
| usbmux `Connect` to lockdownd 62078 | Result 0 |
| usbmux `Connect` to the app's 47820, app using `NWListener` | **Result 3 (refused)**. Wi-Fi to the same listener worked |
| Wi-Fi TCP 192.168.1.20:47820 | Connected, 80,095-byte 1920x1080 JPEG |
| usbmux to 47820 after replacing `NWListener` with a POSIX socket bound to INADDR_ANY | OK: 428,802-byte 1920x1080 JPEG in **66 ms** (connect + fetch) |

`NWListener` is not reachable through usbmux. A BSD socket bound to INADDR_ANY works for both USB and Wi-Fi.

## Environment notes

- PC: Windows 11 build 26200. C: has only about 2 GB free, so MSVC (VS 2026 Build Tools, `D:\VS\BuildTools`) and the Windows SDK (`D:\WindowsKits`) are on D:. This is a risk for Phase 1.
- Not verified in Phase 0: screen-lock behavior of the phone app, PC-free SideStore refresh, and two apps using the camera at once (owner check).

## Decisions

| # | Decision | Evidence |
|---|---|---|
| D17 | Shared memory and event use the `Global\` namespace, DACL `D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;IU)`. Created by the DLL inside Frame Server, opened by the user-mode writer. | Virtual camera section |
| D18 | DirectShow fallback **not needed**: Chrome, Edge, Zoom, Google Meet and Discord all show the camera. | Step 7 table |
| D19 | iOS pipeline confirmed: GitHub Actions `macos-15` + XcodeGen builds an unsigned .ipa, green on the first attempt. | iOS build pipeline |
| D20 | Install route: iloader (Windows, free Apple ID) "Import IPA", re-install from the PC every 7 days. SideStore PC-free refresh is blocked by the upstream -80045 bug; revisit later. | Install route |
| D21 | USB tunnel confirmed: Apple Devices usbmuxd on 127.0.0.1:27015, 66 ms for a 1080p JPEG. Needs the Apple Devices app run once. | USB tunnel |
| D22 | The iOS listener must be a **POSIX/BSD socket** on INADDR_ANY, not `NWListener`. | USB tunnel |

## Implications for Phase 1

- Virtual camera: double buffering or a sequence lock (tearing); timer-driven frame delivery instead of sleeping in `RequestSample`; the installer does the one-time elevated HKLM registration; Frame Server holds the DLL 35-50 s after use, so updates and rebuilds must handle the locked file.
- A second consumer fails cleanly, so the app can show an "in use" state instead of hanging.
- iOS: BSD-socket listener; Bonjour can be advertised separately.
- Check free space on C: before installing anything large.

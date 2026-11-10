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

### Step 7 — owner app checks (to be filled in by the owner)

Run `registrar.exe` (keep it open), then check each app with the writer stopped (grey + moving block) and running (color bars + moving bar).

| # | App | Writer stopped: animates? | Writer running: color bars? | Notes / screenshot |
|---|---|---|---|---|
| 1 | Zoom desktop → Settings → Video | | | |
| 2 | Chrome → webcamtests.com (or Meet preview) | | | |
| 3 | Edge → same | | | |
| 4 | Discord → Voice & Video preview | | | |
| 5 | Camera app + Chrome at the same time | | | |
| 6 | Slack huddle camera (optional) | | | |

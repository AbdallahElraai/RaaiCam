# Phase 1A owner checklist: RaaiCam virtual camera

Date: 2026-11-12   Tester: owner   Overall result (pass / fail): pass, with 1 item pending (step 6, reboot not yet done)

## Automated checks first (optional, 1 minute)

1. Open PowerShell in the project folder: press the Windows key, type `PowerShell`, press Enter, then run:

       cd D:\RaaiCam

2. Run the checks (this stops any running `raaicam-dev` and `vcam-probe`; close apps that use the camera first):

       powershell -ExecutionPolicy Bypass -File windows\tools\run-vcam-checks.ps1

   Expect `A` to `E` PASS. `F` says SKIP unless PowerShell was opened as Administrator (automated check F was not run for this sign-off: it needs elevation). Details are saved under `windows\out\checks\`.

## How to start

In the same PowerShell window (folder `D:\RaaiCam`), start the test-pattern feeder (no phone needed) and leave the window open:

    windows\tools\raaicam-dev\bin\Release\net10.0-windows\raaicam-dev.exe

or `dotnet run --project windows/tools/raaicam-dev -c Release`. It prints one status line per second, for example
`consumer=active 1280x720 gen=1 client(best-effort,unreliable)=none fps=30.0 written=...`.
If the exe is missing, build first: `powershell -ExecutionPolicy Bypass -File windows\build.ps1`.
In apps the camera is named **RaaiCam**; Windows may list it as "RaaiCam (Windows Virtual Camera)".

## Owner steps

| # | Step | What you should see | Pass / Fail | Notes |
|---|------|---------------------|-------------|-------|
| 1 | Start `raaicam-dev` (see above) | Prints `raaicam-dev: running` and then `consumer=closed` or `consumer=inactive` lines | PASS | `consumer=closed` |
| 2 | In Zoom, Chrome (Meet preview), Edge and Discord, pick the camera **RaaiCam** | Moving color bars and a moving white block, no tearing; `raaicam-dev` shows `consumer=active`. `client(best-effort,unreliable)=` is expected to stay `none` (known gap, FR-VC-6; Windows does not record virtual-camera use in the registry) | PASS | Zoom: ok / Chrome (Meet): ok / Edge: ok / Discord: ok; 1080p and 720p; `client=` always none |
| 3 | Close `raaicam-dev` (Ctrl+C) while an app shows the camera | The apps show the dark placeholder card (not black, no frozen picture) | PASS | Card appears; video recovers when the feeder restarts |
| 4 | Open the camera in two apps at once | Note what the second app shows (live video, card, or an error) | PASS (behavior noted) | Second app takes the stream; first app shows a plain dark preview; no error, no hang. Cause not yet diagnosed |
| 5 | Lock and unlock Windows (Win+L) with an app using the camera | Video continues after unlock | PASS | |
| 6 | Reboot the PC | "RaaiCam" is still listed as a camera (System lifetime) | PENDING | Reboot not yet done |

# Phase 1B owner checklist: live iPhone video in the RaaiCam camera

Date: ____   Tester: ____   Overall result (pass / fail): ____

Landscape on the stand, USB cable plugged into the PC. The camera is named **RaaiCam** in apps.

## Step 0: install the new iPhone app (once)

1. On the iPhone, delete the old **RaaiCam Spike** app (press and hold the icon, Remove App, Delete App).
2. On the PC, open **iloader**, sign in, and choose **Import IPA**.
3. Pick this file: `D:\RaaiCam\spikes\out\RaaiCam.ipa` (Claude already put it there; you do not need to download anything).
4. On the iPhone, open **RaaiCam**. When it asks for the camera, tap **Allow**. Allow local network too if asked.
5. Plug the iPhone into the PC with the cable (tap **Trust** if asked).
6. Check the iPhone screen says **Ready**.

## Automated checks first (optional, about 3 minutes)

1. Open PowerShell: press the Windows key, type `PowerShell`, press Enter, then run:

       cd D:\RaaiCam

2. Keep the iPhone unlocked with RaaiCam open on **Ready**. Close apps that use the camera. Run:

       powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1

   It stops any running `raaicam-host`, `raaicam-dev` and `vcam-probe`. If something is missing it says what to do and exits with code 2.
3. Expect `P1` to `P4` PASS and `P5` SKIP. For the 10-minute soak add `-Soak`:

       powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1 -Soak

   Details are saved under `windows\out\phone-checks\`.

## How to start

In PowerShell (folder `D:\RaaiCam`), start the host and leave the window open:

    windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe

It prints one line per second, for example
`link=usb phone=iPhone consumer=1920x1080 in=30.0 out=30.0 lat=48/71ms rtt=3ms 8.0Mbps thermal=nominal batt=82%+`.
If the exe is missing, build first: `powershell -ExecutionPolicy Bypass -File windows\build.ps1`.
Then open Zoom, Google Meet (in Chrome) or Discord and pick the camera **RaaiCam**.
Logs: `%LOCALAPPDATA%\RaaiCam\logs\` (press Win+R, paste that path, Enter).

## Owner steps

| # | Step | What you should see | Pass / Fail | Notes |
|---|------|---------------------|-------------|-------|
| 1 | Open a camera preview in Zoom, Meet and Discord (one at a time) and pick **RaaiCam** | Live video from the iPhone, upright (not sideways or upside down), sharp, no tearing | | |
| 2 | Watch the iPhone screen while you open a meeting, then close it | **Ready** changes to **Streaming** (screen goes dark, tiny red dot) when the meeting opens, and back to **Ready** when it closes | | |
| 3 | Tap the iPhone screen, tap **Pause camera** | The meeting shows the dark card. Tap again (unpause): video comes back | | |
| 3b | Tap **Switch lens** | The view gets wider (ultra-wide). Tap again: back to normal | | |
| 4 | Press the iPhone side button (lock) during a meeting | The meeting shows the card within 1 second. Unlock the phone and open RaaiCam: video is back within 3 seconds | | |
| 5 | Unplug the USB cable during a meeting | The meeting shows the card, no error window on the PC. Plug it back in: video is back within 3 seconds | | |
| 6 | Keep a meeting running for 30 minutes | Note the phone temperature word shown in the host line (`thermal=`) at the end, and any stutter or freeze. `nominal` = cool, fine. `fair` = a bit warm, fine. `serious` = hot, the phone may slow down: note it. `critical` = very hot, stop and let it cool | | thermal=____ Stutter: ____ |

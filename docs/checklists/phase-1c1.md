# Phase 1C-1 owner checklist: pairing, Wi-Fi and automatic switching

Date: ____   Tester: ____   Overall result (pass / fail): ____

What is new: the iPhone now **pairs** with your PC once (scan a code), and then the video can travel over **USB or Wi-Fi**. If you unplug the cable, the video should carry on over Wi-Fi by itself.

Before you start: the PC and the iPhone must be on the **same Wi-Fi network** (not a guest network, no VPN). The camera is named **RaaiCam** in apps.

For every step: do what it says, compare with "What you should see", and if it is different write what happened in "Notes" (and tell Claude).

Open PowerShell once (press the Windows key, type `PowerShell`, press Enter) and run:

    cd D:\RaaiCam

The host program used below is called `raaicam-host`. Its full path is:

    windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe

If that file is missing, build first: `powershell -ExecutionPolicy Bypass -File windows\build.ps1`

## Step 0: install the new iPhone app

Same as Phase 1B, step 0 (`docs\checklists\phase-1b.md`): delete the old RaaiCam app from the iPhone, open **iloader** on the PC, **Import IPA**, pick the new `RaaiCam.ipa` Claude tells you about, open the app on the iPhone, tap **Allow** for the camera (and for the local network if asked). The first screen should say **Pair with your PC**.

## Step 1: Windows Firewall question (only the first time)

The first time `raaicam-host` runs, Windows may show a box "Windows Defender Firewall has blocked some features of this app".

- Tick **Private networks** (your home or office Wi-Fi) and click **Allow access**. You do not need to tick Public networks.
- If you clicked Cancel by mistake, Wi-Fi will not work. Fix: Windows Settings, Privacy & security, Windows Security, Firewall & network protection, Allow an app through firewall, Change settings, find **raaicam-host**, tick **Private**.

## Owner steps

| # | Step | What you should see | Pass / Fail | Notes |
|---|------|---------------------|-------------|-------|
| 2 | Plug the iPhone in with its cable, unlock it, open **RaaiCam**. In PowerShell run `windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe --pair` | A black-and-white square code appears in the PowerShell window, with the text "Scan this with the RaaiCam app on your iPhone (valid 5 minutes)". If the code looks like garbled letters instead, close PowerShell and use **Windows Terminal** instead. Report: a screenshot | | |
| 3 | Point the iPhone at the code (the app shows its camera view; hold it about 30 cm away) | The iPhone says "Waiting for the PC…" and then "Paired with <your PC name>" within a few seconds. PowerShell prints `Paired with <your iPhone name>` and the command finishes. Report: which message you saw last, and any message in orange on the iPhone | | |
| 4 | Run the host with no options: `windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe`. Leave it running, cable still plugged in | One line per second like `link=usb standby=wifi kbps=8000 phone=<iPhone name> ...`. `link=usb` means video travels by cable. `standby=wifi` means Wi-Fi is ready as a backup (it can take up to 30 seconds to appear). Report: the line you see after 1 minute if it never says `standby=wifi` | | |
| 5 | Open Zoom, Meet or Discord and pick the camera **RaaiCam**. Then press Ctrl+C in PowerShell to stop the host | Live video from the iPhone, upright and sharp. After Ctrl+C the host says "stopped" | | |
| 6 | **USB checks** (about 3 minutes). Cable plugged in, iPhone unlocked on **Ready**: `powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1` | `P1` to `P4` say PASS and `P5` says SKIP. If it stops with "PREFLIGHT FAILED" it tells you what to do. Report: the whole text of any FAIL line | | |
| 7 | **Wi-Fi only (W1).** Unplug the cable. Keep the iPhone unlocked with RaaiCam open. Run `powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1 -WifiOnly` | `W1a`, `W1b`, `W1d` say PASS. `W1c` shows the delay (latency); PASS, or WARN if above 150 ms (a WARN is not a failure, just tell Claude the numbers). Takes about 2 minutes. Report: the whole text of any FAIL or WARN line | | |
| 8 | **Automatic switch (F1).** Plug the cable back in. Run `powershell -ExecutionPolicy Bypass -File windows\tools\run-phone-checks.ps1 -Failover`. When it says **UNPLUG THE USB CABLE NOW**, pull the cable out, then press Enter. When it says **PLUG THE USB CABLE BACK IN**, plug it in, then press Enter | `F1a` to `F1f` all say PASS: the video moved to Wi-Fi within about 1 second with no black "card" frame, and moved back to USB within 10 seconds, again with no card. Report: the whole text of any FAIL line. The files are in `windows\out\phone-checks\` (newest folder) | | |
| 9 | **Switching back to USB (by eye).** Start the host, open a camera preview (Zoom or Meet). Unplug the cable, wait 10 seconds, plug it back in, and watch the preview | When the cable goes back in, the picture does not freeze or jump for longer than a blink | | Freeze length: ____ |
| 10 | **Wi-Fi quality (by eye).** Same as step 9, watch the preview while the switch back to USB happens | No sudden blur then sharp, no stutter or "slideshow" moment during the switch | | |
| 11 | **Wi-Fi backup does not hurt the phone.** Cable in, host running, camera open in a meeting for 10 minutes | The iPhone does not get noticeably hotter than with the cable only, and the battery percentage in the host line (`batt=`) does not fall (it should stay the same or go up while charging). Report: `thermal=` and `batt=` at the start and at 10 minutes | | thermal=____ batt=____ |
| 12 | **Unpair from the PC.** With the iPhone plugged in and RaaiCam open: `windows\tools\raaicam-host\bin\Release\net10.0-windows\raaicam-host.exe --unpair` | PowerShell says "Unpaired on this PC and on the phone." and the iPhone goes back to **Pair with your PC**. If it says the phone was not reachable, the PC is unpaired but you must also unpair in the app (step 13) | | |
| 13 | **Unpair from the phone.** Pair again (steps 2 and 3). On the iPhone tap the screen, then **Unpair this PC**, then **Unpair** | The iPhone goes back to **Pair with your PC**. Then run the host with no options: it should say "no phone is paired with this PC. Run: raaicam-host --pair" and stop (it does not wait forever) | | |
| 14 | **A stranger cannot connect.** Pair again. If you have a second PC or second iPhone with RaaiCam: start the host on the second PC (not paired) or open RaaiCam on the second iPhone (not paired) while the first pair is running | The second PC says it is not paired and stops. The second iPhone is never used: your video does not change and the host line still shows your own iPhone's name | | Skip if you have no second device |

## If something is wrong

- Logs: press Win+R, paste `%LOCALAPPDATA%\RaaiCam\logs\`, press Enter. Send Claude the newest file.
- The pairing code is only valid for 5 minutes and only works once. If it expired, just run `--pair` again.
- The host never shows `standby=wifi`: check the same-Wi-Fi rule at the top and the Firewall step (step 1).

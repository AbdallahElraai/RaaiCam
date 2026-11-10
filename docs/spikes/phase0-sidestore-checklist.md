# Phase 0: Install the spike app on your iPhone (SideStore)

**Goal:** get the `RaaiCamSpike` test app running on your iPhone for free, with a free Apple ID, and confirm it can refresh itself without the PC.

**Time:** about 20–30 minutes, once.

**You will type your Apple ID password only into iloader and SideStore yourself.** Claude never sees or handles it. Tip: you can create a separate free Apple ID just for sideloading.

Sources: [SideStore install guide](https://docs.sidestore.io/docs/installation/install), [prerequisites](https://docs.sidestore.io/docs/installation/prerequisites) (checked 2026-11-10).

---

## Part A: On the PC

- [ ] **A1. Apple device support.** Already done: the Apple Devices app is installed. Open **Apple Devices** once from the Start menu, plug in the iPhone with a cable, tap **Trust** on the iPhone and enter your passcode. You should see the iPhone in Apple Devices.
  - SideStore recommends iTunes from Apple's site (<https://www.apple.com/itunes/download/win64>) if Apple Devices doesn't work. Only install it if step B2 fails.
- [ ] **A2. Install iloader.** Download and run the MSI installer from <https://github.com/nab138/iloader/releases/latest/download/iloader-windows-x64.msi> (official site: <https://iloader.app>).
- [ ] **A3. Get the spike app file.** Claude has already downloaded `RaaiCamSpike.ipa` into `D:\RaaiCam\spikes\out\`. You'll send it to the phone in Part C.

## Part B: Install SideStore

- [ ] **B1. On the iPhone, install LocalDevVPN** from the App Store: <https://apps.apple.com/app/localdevvpn/id6755608044>. Open it, tap **Connect**, and allow the VPN configuration.
- [ ] **B2. In iloader on the PC:** sign in with your Apple ID (the email is case-sensitive), select your iPhone, and choose **Install SideStore (Stable)**.
- [ ] **B3. On the iPhone:** Settings → General → **VPN & Device Management** → tap your Apple ID under Developer App → **Trust**.
- [ ] **B4. On the iPhone:** Settings → Privacy & Security → **Developer Mode** → On. The phone restarts; confirm when asked.
- [ ] **B5.** Open **SideStore** and sign in with the **same** Apple ID.

## Part C: Install the RaaiCam spike app

- [ ] **C1. Send the .ipa to the iPhone.** Easiest: email it to yourself or put it in iCloud Drive or OneDrive, then on the iPhone tap the file → Share → **Save to Files**.
- [ ] **C2.** Make sure **LocalDevVPN** is connected. In SideStore → **My Apps** → tap **+** → pick `RaaiCamSpike.ipa`.
- [ ] **C3.** Open **RaaiCam Spike** and tap **Allow** for the camera (and for local network, if asked).
- [ ] **C4.** The screen should show `Camera: running` and `Listener: ready on 47820`. Take a screenshot.
- [ ] **C5.** In SideStore → My Apps, note how many days are left for RaaiCam Spike (it should say 7 DAYS).

## Part D: Quick checks (tell Claude the results)

- [ ] **D1. USB frame test.** Phone plugged in, RaaiCam Spike open → tell Claude, who runs the USB test from the PC.
- [ ] **D2. Screen lock.** Lock the phone for 10 seconds and unlock it. Does RaaiCam Spike still say "ready on 47820"?
- [ ] **D3. Refresh without the PC.** Turn the PC off (or unplug the phone), keep the phone on Wi-Fi, connect LocalDevVPN, and in SideStore tap the **7 DAYS** counter → Refresh. Did it work?

## Results to report back

| Item | Result |
|---|---|
| iPhone model and iOS version (Settings → General → About) | |
| A1 Apple Devices sees the phone | |
| B2 SideStore installed via iloader | |
| C4 App shows camera running and listener ready | |
| C5 Days remaining | |
| D2 Still listening after screen lock | |
| D3 Refresh worked without the PC | |
| Anything confusing or broken | |

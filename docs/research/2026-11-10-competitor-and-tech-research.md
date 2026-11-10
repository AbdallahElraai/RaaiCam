# RaaiCam — Competitor & Technology Research

Date: 2026-11-10 · Context: iPhone 11/12 → Windows 11 PC (GTX 1660 Super), remote-work meetings, $0, 100% local.

## 1. Competitor landscape

| Product | Platforms | Model | Strengths | Weaknesses / gaps for us |
|---|---|---|---|---|
| **DroidCam** | iOS/Android → Win/Linux (+OBS plugin) | Free = 640×480; HD + controls paid | Simple, USB + Wi-Fi, OBS plugin, actively updated (v9.x 2026) | SD-only free tier; dated UI; no AI framing/blur; basic controls |
| **Iriun** | iOS/Android → Win/Mac/Linux | Free w/ limits, paid upgrade | Very simple, good low-light image | Not plug-and-play on Windows; Wi-Fi/firewall issues; few controls; no AI |
| **Camo (Reincubate)** | iOS/Android → Win/Mac | Freemium, ~$40/yr Pro | Most polished; LUTs, overlays, face-focus, 4K, Fluent UI on Windows | Subscription; best AI features paywalled; heavier app |
| **EpocCam (Elgato)** | iOS → Win/Mac | Paid | Brand trust | Stagnant development |
| **Apple Continuity Camera** | iPhone → Mac only | Free | Center Stage, Portrait, Studio Light, Desk View, zero setup | **Not available on Windows** — our core gap |
| **NVIDIA Broadcast** | Any webcam → Windows | Free | Excellent blur, framing, eye contact | **Requires RTX (Tensor cores); GTX 1660 Super is not supported** |
| **OBS Virtual Camera** | Windows | Free | Flexible | Not a camera solution; complex for meetings |

### Takeaways
1. On Windows, nobody combines **iPhone camera + Center-Stage-like framing + good blur + full manual control** for free. Continuity Camera's feature set doesn't exist on Windows, and NVIDIA Broadcast excludes GTX cards.
2. Common user pain: **setup friction** (firewall, start order, drivers), **unreliable Wi-Fi**, **low free resolution**, **cluttered/dated UIs**.
3. Our differentiators: zero-friction pairing (QR + auto USB/Wi-Fi failover), calm cinematic framing, quality blur on non-RTX GPUs, full pro controls, per-app presets, live health stats — all free and local.

## 2. Technology findings

### Virtual camera (Windows 11)
- `MFCreateVirtualCamera` (Win11 build 22000+) registers a Media Foundation virtual camera that Frame Server exposes to **all** apps, including browsers (Chrome/Edge for Google Meet).
- Reference: `smourier/VCamSample` (C++) and `VCamNetSample` (.NET). Source DLL must be accessible to Frame Server services (LocalService/LocalSystem) — install location/ACLs matter.
- Per-user registration needs no admin; all-users needs admin.
- **Risk:** no public confirmation that Zoom desktop sees MF virtual cameras reliably → **must validate in a Phase 0 spike.** Fallback: DirectShow source filter (what OBS uses) for Zoom/Discord.
- Users must have Windows "Let desktop apps access your camera" enabled → our onboarding checks this.

### USB transport
- On Windows, Apple's **Apple Mobile Device Service** (from the free "Apple Devices" app or iTunes) runs usbmuxd on `127.0.0.1:27015`.
- No C# library found; we'll implement the (small, documented) usbmux protocol directly in C# — connect to 27015, `Listen` for device attach, `Connect` to a TCP port the iPhone app listens on. Same approach as libimobiledevice / Rust `idevice`.

### Free sideloading ($0, "always works")
- Free Apple ID: apps expire after **7 days**, max 3 sideloaded apps.
- **AltStore + AltServer (Windows):** auto-refreshes over Wi-Fi while AltServer runs on the PC — fits us since the PC companion is always around. Needs Apple's iTunes/iCloud (non-Store) components.
- **SideStore:** refreshes on-device via a loopback VPN profile, no PC needed after setup; installed from Windows via iLoader/AltServer.
- Recommendation: SideStore primary (PC-free refresh), AltServer as fallback. Our Windows app can show a "certificate expires in N days" warning (iPhone app reports its own expiry).
- Build: GitHub Actions macOS runner (free/unlimited for public repo) builds an **unsigned .ipa**; signing happens on-device/PC by SideStore/AltStore.

### AI on GTX 1660 Super (Turing, no Tensor cores, 6 GB)
- Runtime: **ONNX Runtime** with **DirectML** (works on any DX12 GPU) or **CUDA** EP. FP16 runs but without Tensor-core speedup.
- **Segmentation (blur):** MediaPipe Selfie Segmentation (~0.1M params, 256×256 — very fast, edges average) for v1 baseline; evaluate **Robust Video Matting** (MobileNetV3, temporally stable, better hair) — needs benchmarking at ~512px on the 1660S. Edge refinement via guided filter at full res.
- **Face tracking (framing):** lightweight face detector (e.g. BlazeFace/YuNet/SCRFD-500M, ONNX) + Kalman smoothing + dead-zone controller. iPhone can additionally send AVFoundation face metadata for free (used for on-phone auto-focus/exposure on face).
- **Eye contact (v3):** no RTX → open-source gaze-redirection models; quality uncertain; lab feature.

### Video pipeline
- iPhone: AVCaptureSession (rear wide/ultra-wide) → VideoToolbox H.264/HEVC hardware encode, low-latency config (no B-frames, short GOP, real-time).
- PC: Media Foundation hardware decode (NVDEC via D3D11) → D3D11 textures → AI + compositing on GPU (compute shaders) → virtual camera NV12 frames.
- Target glass-to-glass latency: **< 100 ms USB, < 150 ms Wi-Fi.**

## 3. Feature opportunities beyond competitors (for later phases)
- **Setup doctor:** one-click diagnosis (Apple Mobile Device Service present? firewall rule? camera privacy toggle? same Wi-Fi?) — directly attacks the #1 competitor complaint.
- **Smart start:** virtual camera shows a tasteful "camera waking…" card instead of black, then fades in.
- **Thermal-aware adaptive quality** with visible phone temperature.
- **Meeting-aware presets** (auto-apply per app).
- **Expiry guard** for the free signing certificate.
- **Lens-aware framing:** use ultra-wide when you move far, switch to wide for close-ups (seamless crossfade).

## Sources
- [DroidCam App Store](https://apps.apple.com/us/app/-/id1510258102) · [DroidCam client (SourceForge)](https://sourceforge.net/app/droidcam-client/) · [AlternativeTo: DroidCam](https://alternativeto.net/software/droidcam)
- [Iriun troubleshooting](https://iriun.github.io/troubleshooting.html) · [Webcamtests Iriun review](https://webcamtests.com/reviews/209220) · [AlternativeTo Iriun](https://alternativeto.net/software/iriun-webcam/about)
- [Camo App Store](https://apps.apple.com/app/id1514199064) · [Macworld Camo review](https://www.macworld.com/article/3568492/camo-review.html) · [Camo Studio (SourceForge)](https://sourceforge.net/app/camo-studio/)
- [MFCreateVirtualCamera docs](https://learn.microsoft.com/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera) · [VCamSample](https://github-redirect.dependabot.com/smourier/VCamSample)
- [libusbmuxd README](https://cgit.libimobiledevice.org/libusbmuxd.git/tree/README.md) · [usbmuxd README](https://cgit.libimobiledevice.org/usbmuxd.git/tree/README?h=v1.0.0) · [idevice crate](https://docs.rs/crate/idevice/0.1.61/source/src/usbmuxd/mod.rs)
- [SideStore PC-less refresh guide](https://kitsah.bearblog.dev/how-to-setup-sidestore-with-automatic-pc-less-refreshing/) · [AltStore vs Sideloadly](https://unanswered.io/guide/altstore-vs-sideloadly)
- [MediaPipe Selfie Segmentation (HF)](https://huggingface.co/qualcomm/MediaPipe-Selfie-Segmentation) · [TF body segmentation blog](https://blog.tensorflow.org/2022/01/body-segmentation.html?hl=th) · [Luxonis ONNX model](https://models.luxonis.com/luxonis/mediapipe-selfie-segmentation/aim_2d3cPJ1DDXFDZqwX5jX3n8?backTo=%2F)
- [NVIDIA Broadcast release notes](https://www.nvidia.com/en-in/geforce/broadcasting/broadcast-app/release-notes) · [Tom's Hardware Eye Contact test](https://tomshardware.com/news/testing-nvidia-broadcast-eye-contact)

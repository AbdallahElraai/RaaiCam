# Phase 1C-1 — Secure Pairing, Encrypted Wi-Fi, USB↔Wi-Fi Failover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pair the iPhone with the PC once by QR code, then stream over USB or Wi-Fi with mutual authentication and encryption, switching between them in under 1 s without visible interruption (FR-CON-1..7, FR-STR-4 bitrate part).

**Architecture:** Protocol v2 adds an AUTH handshake (X25519 ECDH + HKDF-SHA256 → AES-256-GCM per-direction keys) that every connection (USB and Wi-Fi) must complete before any other frame. The phone accepts at most **two** authenticated sessions from its paired PC at once — one loopback (USB) and one network (Wi-Fi) — and sends VIDEO only on the session the PC marks `route video=true`. The PC keeps both warm, prefers USB, and on USB loss re-routes video to Wi-Fi and requests a keyframe. Discovery is Bonjour (`_raaicam._tcp`) + last-known IP. Pairing: the PC shows a QR (console in 1C-1, tray panel in 1C-2); the phone scans it and completes a token-authenticated first handshake that exchanges long-term keys.

**Tech Stack:** C# .NET 10 (`System.Security.Cryptography`: `ECDiffieHellman` with `ECCurve.CreateFromFriendlyName("curve25519")` via Windows CNG, `HKDF`, `AesGcm`, `ProtectedData` DPAPI), QRCoder (pinned), an mDNS browser library (pinned, MIT/Apache) ; Swift CryptoKit (`Curve25519.KeyAgreement`, `HKDF<SHA256>`, `AES.GCM`), Keychain, `AVCaptureMetadataOutput` QR scanning, `NetService` publish.

**Spec:** `docs/superpowers/specs/2026-11-10-raaicam-design.md` FR-CON-1..7, FR-STR-4, §9.4, §9.5 (pairing storage), D22 (authenticate before video on Wi-Fi) · builds on Phase 1B (`protocol/PROTOCOL.md` v1, `ios/`, `RaaiCam.Core`, `raaicam-host`).

## Global Constraints

- **Protocol version 2** (`"proto":2`); v1 peers are rejected with a LOG + close. HELLO stays plaintext but is reduced to `{"proto":2,"role":…}` (no device/os names until after AUTH; they move into the encrypted `INFO` frame).
- **No frame other than HELLO/AUTH is processed before AUTH succeeds** on any link, USB included (uniform security).
- **Crypto:** X25519 (static + ephemeral both sides), HKDF-SHA256, AES-256-GCM with 96-bit nonces = 32-bit direction id ‖ 64-bit counter; counters never repeat per key; a frame failing authentication closes the session. Keys rotate per connection (new ephemerals). No custom primitives.
- **Pairing:** QR payload = `raaicam://pair?v=2&pc=<pcId base64url>&name=<urlencoded>&pk=<PC static pub base64url>&tok=<16-byte one-time token base64url>&exp=<unix seconds>`; token valid **5 min**, single use. LAN IPs are NOT in the QR (the phone is the listener; the PC finds the phone). One phone paired per PC and one PC per phone; re-pairing replaces the old pairing on both sides. Unpair from either side.
- **Storage:** PC `%APPDATA%\RaaiCam\pairing.bin` DPAPI (CurrentUser) protected; phone Keychain item `com.raaicam.app.pairing` (`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`). Never log keys, tokens or the QR string.
- **Phone listener:** BSD socket 47820 as in 1B; sessions: ≤1 loopback + ≤1 non-loopback, both must be the paired PC (or, while a pairing token is pending, the pairing PC). Unpaired phone: accepts loopback + network connections only for the token-authenticated pairing handshake.
- **Bonjour:** phone publishes `_raaicam._tcp` on port 47820 with TXT `v=2` and `id=<phone pairing id hash, 8 hex>` only (no device name, no keys) **only while paired or pairing**.
- **Failover (FR-CON-5):** USB preferred; Wi-Fi session kept authenticated and idle (PING only) while USB streams; on USB loss re-route within **≤ 1 s** of the loss being detected with no card shown (the sink's 500 ms Reused window covers the gap); when USB returns, switch back at the next keyframe.
- **Wi-Fi bitrate adaptation (FR-STR-4, bitrate only — resolution follows the consumer):** start 6000 kbps on Wi-Fi; step down 6000→4000→2500 when decode backlog > 2 frames or RTT p95 > 150 ms for 2 s; step up one level after 10 s stable; USB stays 8000.
- **Reconnect (FR-CON-6):** backoff 0.5 s → 5 s, indefinitely while the host runs.
- **Video over Wi-Fi uses TCP** in 1C-1 (UDP deferred; record as a spec deviation with a revisit trigger: Wi-Fi p95 latency > 150 ms in the owner test).
- Builds/push/commit rules as Phase 1B (branch `phase1c-pairing-wifi` may be pushed for CI; never force-push; `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`); C: has ~2 GB free; exact pinned packages.

## Review Focus

1. **Replay / reflection / downgrade:** a recorded AUTH or a v1 HELLO must not authenticate; tests `Auth_ReplayedTranscriptRejected`, `Auth_V1HelloRejected`, `Auth_ReflectedMessageRejected` (C#) and the Swift equivalents.
2. **Expired or reused pairing token:** rejected on the phone; PC shows a new QR. Tests `Pairing_ExpiredTokenRejected`, `Pairing_TokenSingleUse`.
3. **Nonce reuse / counter overflow / tampered frame:** tampered byte → session closed; counter never repeats; tests `Cipher_TamperClosesSession`, `Cipher_NoncesStrictlyIncrease`.
4. **Pulling the USB cable mid-meeting while Wi-Fi is up:** no card visible, video continues within 1 s. Test `Failover_UsbLossRoutesToWifiWithin1s` (fakes) + owner checklist.
5. **Phone on a different/foreign Wi-Fi or another PC on the LAN probing 47820:** unauthenticated network peers get nothing beyond the HELLO and are closed within 5 s; tests `Phone_UnpairedNetworkPeerClosed` (Swift FrameServer test) and `Host_IgnoresOtherPhones`.

---

## Protocol v2 delta (normative: update `protocol/PROTOCOL.md`)

New/changed frame types: `0x01 HELLO {"proto":2,"role":"pc"|"phone"}` · `0x02 AUTH` (handshake messages below, CBOR-free: fixed binary layouts) · `0x09 SEALED` (`[u64 counter][ciphertext‖16-byte tag]`; plaintext = an inner v1 frame `[u8 type][u32 len][payload]`) · `0x0A INFO` (inner only: `{"app","version","device","os"}`).

Handshake (PC = initiator `I`, phone = responder `R`; `S_*` static, `E_*` ephemeral X25519 keys):
1. `I→R AUTH1`: `[u8 mode (1=paired, 2=pairing)][32 E_I.pub][32 S_I.pub][16 pcId]` (+ for mode 2: `[16 HMAC-SHA256(token, "raaicam-pair-v2"‖E_I.pub‖S_I.pub)[0..16]]`).
2. `R→I AUTH2`: `[32 E_R.pub][ciphertext of {S_R.pub (32)}‖tag]` encrypted with `k0 = HKDF(ikm = DH(E_I,E_R)‖DH(S_I,E_R), salt = "raaicam-v2", info = "auth2" ‖ transcript-hash)`.
3. `I→R AUTH3`: `[ciphertext of {"ok"}‖tag]` with `k1 = HKDF(DH(E_I,E_R)‖DH(S_I,E_R)‖DH(E_I,S_R), …, "auth3" ‖ th)`.
4. Session keys: `k_i2r, k_r2i = HKDF(DH(E_I,E_R)‖DH(S_I,E_R)‖DH(E_I,S_R)‖DH(S_I,S_R), salt, "session" ‖ th, 64 bytes)`.
`th` = SHA-256 over all handshake bytes so far. Responder checks: mode 1 → `S_I.pub` equals the paired PC key; mode 2 → token valid, unused, unexpired, HMAC matches, `pk` in QR equals `S_I.pub`; then stores `S_I` and the PC stores `S_R` after AUTH3. Any failure → close without detail. Timeout 5 s for the whole handshake.

New commands (inner CMD): `{"cmd":"route","video":true|false}` · `{"cmd":"unpair"}`. Telemetry gains `"link":"usb"|"wifi"`.

`protocol/test-vectors-v2.json`: fixed-key handshake vectors (deterministic ephemerals given as hex) with expected AUTH1/AUTH2/AUTH3 bytes, session keys and one SEALED frame — shared by C# and Swift tests.

---

## File Structure

```
protocol/PROTOCOL.md (v2 section), protocol/test-vectors-v2.json
windows/RaaiCam.Protocol/  Crypto/Handshake.cs, Crypto/SealedChannel.cs, Crypto/X25519.cs  (+ tests)
windows/RaaiCam.Core/      Pairing/PairingStore.cs, Pairing/PairingQr.cs, Discovery/WifiDiscovery.cs,
                           Phone/SecureLink.cs (wraps transport: HELLO→AUTH→SEALED), Hosting/LinkManager.cs (USB+Wi-Fi, route, failover, bitrate)
                           (+ tests)
windows/tools/raaicam-host/ Program.cs: `--pair` (console QR), `--unpair`, status shows link=usb|wifi
ios/Sources/Security/ Handshake.swift, SealedChannel.swift, PairingStore.swift (Keychain)
ios/Sources/Pairing/  PairingView.swift (QR scanner), PairingController.swift
ios/Sources/Net/      FrameServer.swift (two-slot sessions), Bonjour.swift (NetService publish)
ios/Tests/            HandshakeTests.swift, SealedChannelTests.swift, PairingTests.swift, FrameServerTwoSlotTests.swift
docs/checklists/phase-1c1.md
```

---

### Task 1: Protocol v2 spec, vectors, C# handshake + sealed channel

**Files:** `protocol/PROTOCOL.md`, `protocol/test-vectors-v2.json`, `windows/RaaiCam.Protocol/Crypto/*`, tests `windows/RaaiCam.Protocol.Tests/{HandshakeTests,SealedChannelTests}.cs`.

**Interfaces (Produces):**
- `static class X25519 { (byte[] priv, byte[] pub) Generate(); byte[] Agree(byte[] priv, byte[] peerPub); }` — CNG curve25519; plus a deterministic `FromPrivate(byte[])` for vectors.
- `sealed class InitiatorHandshake { InitiatorHandshake(StaticKey pc, byte[] pcId, byte[]? pairedPhonePub, byte[]? token /* mode 2 */); byte[] CreateAuth1(); byte[] ReadAuth2(byte[] auth2) /* returns phone static pub */; byte[] CreateAuth3(); SessionKeys Keys { get; } }`
- `sealed class ResponderHandshake { ResponderHandshake(StaticKey phone, Func<byte[], bool> isPairedPc, PairingTokenCheck? tokenCheck); byte[] ReadAuth1AndCreateAuth2(byte[] auth1); void ReadAuth3(byte[] auth3); SessionKeys Keys; byte[] PeerStatic; bool WasPairing; }` (C# responder exists so C# tests can run both sides; the real responder is Swift).
- `sealed class SealedChannel { SealedChannel(SessionKeys keys, bool isInitiator); byte[] Seal(FrameType innerType, ReadOnlySpan<byte> innerPayload); Frame Open(ReadOnlySpan<byte> sealedPayload) /* throws CryptoProtocolException on tag fail / counter regression */; }`
- `sealed class CryptoProtocolException : ProtocolException`.

- [ ] **Step 1:** Update PROTOCOL.md (v2 section above, verbatim + versioning: v1 is retired). Generate `test-vectors-v2.json` with a throwaway C# script using fixed private keys (document them in the file, clearly test-only).
- [ ] **Step 2: Failing tests:** `Vectors_HandshakeBytesMatch`, `Vectors_SessionKeysMatch`, `Vectors_SealedFrameMatches`, `Auth_PairedModeSucceeds`, `Auth_PairingModeSucceeds_StoresPeer`, `Auth_UnknownPcRejected`, `Auth_ReplayedTranscriptRejected`, `Auth_ReflectedMessageRejected`, `Auth_V1HelloRejected` (codec-level: proto 1 → ProtocolException), `Pairing_ExpiredTokenRejected`, `Pairing_TokenSingleUse`, `Pairing_WrongHmacRejected`, `Cipher_TamperClosesSession`, `Cipher_NoncesStrictlyIncrease`, `Cipher_CounterRegressionRejected`.
- [ ] **Step 3:** FAIL → **Step 4:** implement → **Step 5:** PASS (15) + all existing tests (HELLO tests updated to proto 2 — update `test-vectors.json` HELLO vectors and keep the rest).
- [ ] **Step 6: Commit** `feat(protocol): v2 authenticated handshake and sealed channel`.

### Task 2: iOS security, pairing UI, two-slot server, Bonjour

**Files:** `ios/Sources/Security/*`, `ios/Sources/Pairing/*`, `ios/Sources/Net/{FrameServer,Bonjour}.swift`, `ios/Sources/Session/PhoneSession.swift`, `ios/Sources/App/AppModel.swift` (+ screen `.pairing`), tests as listed; `ios/project.yml` (copy `test-vectors-v2.json`).

**Interfaces:** Swift mirrors of Task 1 (`InitiatorHandshake` not needed on iOS; `ResponderHandshake`, `SealedChannel`, `PairingStore` with `paired: PairedPC?`, `pendingToken: PairingToken?`, `unpair()`).
**Behavior:**
- Unpaired → screen `.pairing`: "Pair with your PC" + QR scanner (rear camera, `AVCaptureMetadataOutput` `.qr`), parses the `raaicam://pair` URL, validates `exp`, stores the pending token (in memory only, 5 min), shows "Waiting for the PC…" and accepts the mode-2 handshake from the PC with that `pk`.
- FrameServer: per-connection state machine HELLO → AUTH → SEALED; slots: loopback slot and network slot (a new connection replaces the same-kind slot only after it authenticates; unauthenticated connections close after 5 s or on any non-handshake frame; network connections refused entirely while unpaired and no pending token).
- Session: VIDEO only on the session with `route video=true` (default: the most recently authenticated loopback session, else the network one); TELEMETRY on both; `unpair` cmd → wipe Keychain + close all + `.pairing`.
- Bonjour: publish while paired or pending; stop when unpaired.
- QuickControls gains "Unpair this PC" (confirmation alert).
- Info.plist: `NSBonjourServices = ["_raaicam._tcp"]`, local-network string kept.
- [ ] **Step 1: Failing tests:** `testHandshakeVectors`, `testSealedVectors`, `testReplayRejected`, `testExpiredTokenRejected`, `testTokenSingleUse`, `testTamperRejected`, `testPairingURLParses`, `testPairingURLRejectsBadVersion`, `testUnpairedNetworkPeerClosed` (loopback FrameServer test with a network-like peer injected via a test seam), `testTwoSlotsVideoOnlyOnRouted`.
- [ ] **Step 2:** push → CI RED → **Step 3:** implement → **Step 4:** CI GREEN, refresh `spikes/out/RaaiCam.ipa` → **Step 5: Commit** `feat(ios): QR pairing, authenticated encrypted sessions, two-slot server, Bonjour`.

### Task 3: PC pairing store, QR, secure link

**Files:** `windows/RaaiCam.Core/Pairing/*`, `windows/RaaiCam.Core/Phone/SecureLink.cs`, `PhoneSession.cs` (use SecureLink), tests.
**Interfaces:**
- `sealed class PairingStore { PairingStore(string path, IDataProtector protector); PcIdentity GetOrCreateIdentity() /* pcId + static key, persisted */; PairedPhone? Phone { get; } void SavePhone(byte[] phoneStaticPub, string label); void Unpair(); }` with `IDataProtector` = DPAPI in production, a fake in tests.
- `sealed class PairingSession { static PairingSession Begin(PcIdentity id, string pcName, TimeProvider t); string QrUrl { get; } byte[] Token { get; } bool IsExpired { get; } }` + `PairingQr.ToConsoleString(string url)` (QRCoder ASCII, two-char modules for square aspect).
- `PhoneSession` gains a constructor taking `SecureLinkOptions { PcIdentity, PairedPhone? or PairingSession? }`; performs HELLO(v2) → AUTH → then all frames via `SealedChannel`; raises `Paired(PairedPhone)` after a successful mode-2 handshake; `SendRouteAsync(bool video)`, `UnpairAsync()`.
- [ ] **Step 1: Failing tests:** `Store_PersistsIdentityAcrossInstances`, `Store_DataIsProtected` (file bytes don't contain the private key), `Store_UnpairDeletes`, `Qr_UrlHasRequiredFieldsAndNoIps`, `Session_FullHandshakeAgainstCSharpResponder`, `Session_PairingModeRaisesPaired`, `Session_TamperedSealedFrameDisconnects`, `Session_RouteCommandSealed`.
- [ ] FAIL → implement → PASS → **Commit** `feat(core): pairing store, QR pairing session, secure link`.

### Task 4: Wi-Fi discovery

**Files:** `windows/RaaiCam.Core/Discovery/WifiDiscovery.cs` + tests; choose and pin an mDNS library (MIT/Apache, maintained) — or implement a minimal mDNS PTR/SRV/A query over UDP 5353 if no suitable library (justify).
**Interfaces:** `sealed class WifiDiscovery : IAsyncDisposable { IAsyncEnumerable<DiscoveredPhone> BrowseAsync(CancellationToken); } record DiscoveredPhone(string IdHash, IPAddress Address, int Port)`; plus `LastKnownAddress` persisted in `%APPDATA%\RaaiCam\settings.json` (`"lastPhoneIp"`), tried first.
- [ ] Failing tests with a fake mDNS responder on loopback multicast or a parser-level test on captured packet bytes (`Discovery_ParsesServiceAnswer`, `Discovery_FiltersByIdHash`, `Discovery_UsesLastKnownFirst`) → implement → PASS → **Commit** `feat(core): Bonjour discovery for the phone`.

### Task 5: Link manager — dual links, routing, failover, bitrate

**Files:** `windows/RaaiCam.Core/Hosting/LinkManager.cs`, `Host.cs` (use LinkManager instead of a single link), tests `LinkManagerTests.cs`.
**Rules:** maintain up to two authenticated sessions (USB via usbmux, Wi-Fi via discovery/last IP) with backoff 0.5→5 s; route video to USB when present else Wi-Fi (`route` cmds + keyframe request on every switch); switch back to USB at the next keyframe; Wi-Fi bitrate ladder per Global Constraints; status `link=usb|wifi|none`, `standby=wifi|usb|none`; ignore phones whose static key isn't the paired one (`Host_IgnoresOtherPhones`).
- [ ] Failing tests (fakes, `FakeTimeProvider`): `Failover_UsbLossRoutesToWifiWithin1s`, `Failover_UsbReturnSwitchesBackAtKeyframe`, `Bitrate_StepsDownOnBacklog`, `Bitrate_StepsUpAfter10sStable`, `Reconnect_BackoffSequence`, `Host_IgnoresOtherPhones`, `Route_OnlyOneSessionCarriesVideo` → implement → PASS → **Commit** `feat(core): dual-link manager with sub-second failover and Wi-Fi bitrate adaptation`.

### Task 6: Host CLI pairing + e2e checks + owner checklist

**Files:** `windows/tools/raaicam-host/Program.cs` (`--pair`: prints the QR in the console, waits up to 5 min for the phone, prints "Paired with <phone>"; `--unpair`; normal run requires a pairing and prints how to pair otherwise), `windows/tools/run-phone-checks.ps1` (add W1 Wi-Fi-only stream check with cable unplugged prompts → since unattended, implement as `-WifiOnly` mode the owner runs with the cable out; F1 failover check: owner unplugs on prompt, script asserts no card frames in the probe log and `link` switches within 1 s), `docs/checklists/phase-1c1.md`.
- [ ] Implement, parse-check, `build.ps1 -Test` green, **Commit** `test(e2e): pairing CLI, Wi-Fi and failover checks, owner checklist`. Hardware runs happen with the owner at the end (combined with Phase 1B's).

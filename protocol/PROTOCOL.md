# RaaiCam Protocol v2

Protocol v1 is **retired**: v2 peers reject a v1 HELLO (LOG + close) and never fall back. v2 adds mutual authentication and encryption (below); the framing and the inner message formats are unchanged from v1.

All integers **big-endian**. Frame: `[u8 type][u32 length][payload]`, `length` = payload bytes, max **4 MiB**; larger → protocol error, close.

| type | name | dir | payload |
|---|---|---|---|
| 0x01 | HELLO | both | UTF-8 JSON `{"proto":2,"role":"phone"\|"pc"}`, the first frame each way, plaintext. No device/OS names (they travel in the encrypted INFO frame after AUTH). proto ≠ 2 → close |
| 0x02 | AUTH | both | handshake message, fixed binary layout (see "Protocol v2 handshake"); exactly 3 per connection |
| 0x03 | CMD | pc→phone | UTF-8 JSON, see commands (inner only after AUTH) |
| 0x04 | TELEMETRY | phone→pc | UTF-8 JSON every 2 s (inner only) |
| 0x05 | LOG | phone→pc | UTF-8 text line (no newline), level prefix `I `/`W `/`E ` (inner only) |
| 0x06 | VIDEO | phone→pc | `[u64 captureUs][u32 seq][u8 flags][Annex-B access unit]`, flags bit0 = keyframe (contains SPS+PPS+IDR) (inner only) |
| 0x07 | PING | pc→phone | `[u64 pcSendUs]` (inner only) |
| 0x08 | PONG | phone→pc | `[u64 pcSendUs][u64 phoneRecvUs]` (inner only) |
| 0x09 | SEALED | both | `[u64 counter][ciphertext ‖ 16-byte tag]`; plaintext = one inner frame `[u8 type][u32 len][payload]` |
| 0x0A | INFO | both | inner only: `{"app","version","device","os"}` |

**No frame other than HELLO and AUTH is processed before the handshake has completed**, on any link (USB included). After AUTH, every frame other than SEALED is a protocol error; the inner type of a SEALED frame must not be HELLO, AUTH or SEALED. A peer that receives anything out of order closes the connection.

Commands (`"cmd"` field): `{"cmd":"start","width":1920,"height":1080,"fps":30,"lens":"wide","bitrateKbps":8000}` · `{"cmd":"stop"}` · `{"cmd":"keyframe"}` · `{"cmd":"bitrate","kbps":6000}` · `{"cmd":"lens","lens":"wide"|"ultra"}` · `{"cmd":"route","video":true|false}` · `{"cmd":"unpair"}`. Unknown cmd → phone replies LOG `W unknown cmd <name>`.

Telemetry: `{"thermal":"nominal"|"fair"|"serious"|"critical","battery":0.0-1.0,"charging":bool,"streaming":bool,"width":int,"height":int,"fpsCapture":float,"fpsEncode":float,"bitrateKbps":int,"lens":"wide"|"ultra","paused":bool,"link":"usb"|"wifi"}`.

Clocks: `captureUs`/`phoneRecvUs` = phone `mach_continuous_time` in µs; `pcSendUs` = PC `Stopwatch` µs. Offset estimate per PONG = `phoneRecvUs − (pcSendUs + rtt/2)`; keep the sample with the smallest RTT in the last 10 s.

## Protocol v2 handshake

The PC is the **initiator** `I`, the phone the **responder** `R`. `S_*` = long-term (static) X25519 key pair, `E_*` = ephemeral X25519 key pair generated fresh for every connection. Public keys are the raw 32-byte RFC 7748 encoding (CryptoKit `Curve25519.KeyAgreement.PublicKey.rawRepresentation`). `DH(a,B)` = the raw 32-byte X25519 shared secret (RFC 7748), `‖` = concatenation. A shared secret of all zeros (low-order peer point) is a failure. The whole handshake has a **5 s** timeout.

Constants: `salt = UTF-8("raaicam-v2")` (10 bytes, no terminator). Labels are ASCII, no terminator.

### Messages (each carried as the payload of one AUTH frame)

1. **AUTH1** (I→R), `[u8 mode][32 E_I.pub][32 S_I.pub][16 pcId]`, plus in mode 2 `[16 pairHmac]`. Length 81 (mode 1, *paired*) or 97 (mode 2, *pairing*); any other length or mode is a failure. `pairHmac = HMAC-SHA256(key = token, data = UTF-8("raaicam-pair-v2") ‖ E_I.pub ‖ S_I.pub)[0..16]`, compared in constant time. `token` is the 16 random bytes from the QR.
2. **AUTH2** (R→I), `[32 E_R.pub][48 bytes: AES-256-GCM(k0, plaintext = S_R.pub (32)) ciphertext(32) ‖ tag(16)]`, length 80.
3. **AUTH3** (I→R), `[AES-256-GCM(k1, plaintext = "ok" (2 bytes)) ciphertext(2) ‖ tag(16)]`, length 18.

### Key schedule

```
th0 = SHA256( "raaicam-v2" ‖ AUTH1 )                    ; ASCII string, then the 81/97 AUTH1 bytes
th1 = SHA256( th0 ‖ E_R.pub )
th2 = SHA256( th1 ‖ AUTH2.ct )                          ; AUTH2.ct = the 48-byte ciphertext‖tag (bytes 32..80 of AUTH2)

HKDF(ikm, label, th, len) = HKDF-SHA256(ikm, salt = "raaicam-v2", info = ASCII(label) ‖ th, length = len)   ; RFC 5869

dhEE = DH(E_I,E_R)   dhSE = DH(S_I,E_R)   dhES = DH(E_I,S_R)   dhSS = DH(S_I,S_R)

k0  = HKDF(dhEE ‖ dhSE,                "auth2",   th1, 32)
k1  = HKDF(dhEE ‖ dhSE ‖ dhES,         "auth3",   th2, 32)
okm = HKDF(dhEE ‖ dhSE ‖ dhES ‖ dhSS,  "session", th2, 64)
k_i2r = okm[0..32]    k_r2i = okm[32..64]
```

`dhSE` is computable by R from AUTH1 (it holds `S_I.pub`); `dhES` and `dhSS` need `S_R.priv`, so only the genuine phone can produce a valid AUTH2/AUTH3 chain; `dhSE` and `dhSS` need `S_I.priv`, so only the genuine PC can.

### AEAD rules

* AUTH2 and AUTH3 use a **12-zero-byte nonce** and **empty associated data**. This is safe because `k0` and `k1` each encrypt exactly one message and are unique per connection (fresh `E_I`, `E_R`).
* SEALED frames use `k_i2r` for I→R and `k_r2i` for R→I. **Nonce** = `[u32 direction][u64 counter]` (12 bytes), direction 1 = I→R, 2 = R→I. **AAD** = `[u8 0x09][u64 counter]`. The counter starts at 0 per connection and direction and increments by 1 per frame; it is the same value that is sent in the clear at the start of the SEALED payload. The sender must close the session before the counter would reach 2^64 − 1.
* The receiver requires `counter == expected next value` (TCP is ordered, so any gap, repeat or regression is an attack or a bug). Any mismatch, tag failure, truncated payload or malformed/forbidden inner frame **closes the session**: the receiver must never accept further frames on it.
* AES-GCM tag = 16 bytes. CryptoKit: `AES.GCM.seal(plain, using: key, nonce: AES.GCM.Nonce(data: nonce12), authenticating: aad)` and carry `ciphertext ‖ tag` (not CryptoKit's `combined` form, whose nonce prefix we do not send).
* `plaintext` of a SEALED frame is exactly one inner frame `[u8 type][u32 len][payload]` where `len` must equal the remaining plaintext length.

### Responder checks

* Mode 1: `S_I.pub` must equal the stored paired PC key.
* Mode 2 (pairing): a token must be pending; `pairHmac` verifies; the token is not expired, not used, and `S_I.pub` equals the `pk` from the QR; no other handshake using the same token is in flight. The token is **consumed only after AUTH3 verifies** (an AUTH1 alone does not burn it, so a network attacker cannot deny pairing; a second AUTH1 with the same token while one handshake is in flight is refused, and the claim is released when that handshake fails, times out or the connection closes). The phone stores `S_I.pub` and the PC stores `S_R.pub` after AUTH3.
* Initiator in mode 1: the `S_R.pub` decrypted from AUTH2 must equal the stored paired phone key.
* Every failure is silent to the peer (close the connection) and logged generically (never key material, tokens or the QR string).

### Pairing QR

`raaicam://pair?v=2&pc=<pcId base64url>&name=<urlencoded>&pk=<PC static pub base64url>&tok=<16-byte one-time token base64url>&exp=<unix seconds>`. Token valid 5 minutes, single use. LAN addresses are not in the QR.

## Security notes

* **Mutual authentication:** the PC proves possession of `S_I.priv` through `dhSE`/`dhSS` (both in `k1`/the session keys) and the phone proves `S_R.priv` through `dhES`/`dhSS`, because a correct AUTH2 decryption and AUTH3 verification are impossible without them. In mode 1 each side also checks the peer static key against its stored pairing.
* **Key-compromise impersonation (KCI) resistance:** leaking `S_I.priv` alone lets an attacker impersonate the PC to the phone, but not the phone to the PC: that requires `dhES = DH(E_I, S_R)`, i.e. `S_R.priv` or `E_I.priv`. Symmetrically `S_R.priv` alone cannot impersonate the PC, since `dhSE = DH(S_I, E_R)` needs `S_I.priv` or `E_R.priv`.
* **Replay resistance:** both sides contribute fresh ephemerals per connection. A recorded AUTH1/AUTH3 replayed to a phone meets a new `E_R`, so the recorded AUTH3 does not verify (test `Auth_ReplayedTranscriptRejected`). A replayed SEALED frame fails the strict counter check.
* **Transcript binding:** AUTH1 is hashed into `th0`, `E_R` into `th1`, and the AUTH2 ciphertext into `th2`; `th1`/`th2` are in the KDF info for `k0`/`k1`/the session keys, so any modification of the handshake yields different keys and a failed AEAD tag (downgrade of the mode byte included, since the mode byte is part of AUTH1).
* **No reflection:** the AUTH messages have different lengths and positions; the keys are direction-separated (`k_i2r` ≠ `k_r2i`), SEALED nonces carry a direction id, and initiator/responder state machines accept only their own next message. A reflected message therefore never decrypts.
* **Version downgrade:** v1 HELLO is rejected and v1 has no path to the encrypted channel.
* **Known non-goals:** (1) no identity hiding for the PC: `S_I.pub` and `pcId` are sent in clear in AUTH1 (the phone identity is hidden from passive observers; an active attacker who knows the paired PC key can still make the phone answer, but cannot read or forge anything). (2) Forward secrecy is limited: session keys depend on the ephemerals, which are deleted after the handshake, but if both static keys **and** a recorded transcript are compromised **before** the ephemerals are erased, past sessions of that connection are exposed. After the ephemerals are gone, past sessions stay secret even if both static keys leak. (3) Traffic analysis (frame sizes and timing) is not hidden. (4) An attacker on the network can always deny service by closing connections.
* **X25519 implementation:** Windows CNG `curve25519` cannot import a raw 32-byte private key in .NET (so no deterministic vectors, no raw interop with CryptoKit), so the C# side uses BouncyCastle.Cryptography's RFC 7748 `X25519` (MIT, pinned exact version) for X25519 only; all other primitives are .NET built-ins (`HKDF`, `AesGcm`, `SHA256`, `HMACSHA256`).

## Test vectors

`protocol/test-vectors.json`: hex encodings of one frame of every v1-era type (incl. a VIDEO with a 16-byte dummy AU) and the v2 HELLOs, shared by the C# and Swift tests.

`protocol/test-vectors-v2.json`: fixed **test-only** private keys (`sI`, `eI`, `sR`, `eR`), `pcId` and `token`, and for both modes the expected AUTH1/AUTH2/AUTH3 bytes and `k_i2r`/`k_r2i`, plus one SEALED frame (paired-mode keys, I→R, counter 0, inner CMD `{"cmd":"stop"}`) as payload and as a full wire frame. Shared by the C# and Swift tests.

## Versioning

The protocol version is the `proto` integer in HELLO. Version 2 is described here; version 1 is retired. Any incompatible change (frame layout, type meanings, required fields, handshake/KDF) bumps `proto`; a peer that receives a HELLO with an unsupported `proto` closes the connection. Additive changes (new optional JSON fields, new commands) do not bump `proto`: receivers ignore unknown JSON fields, and the phone answers unknown commands with a LOG warning.

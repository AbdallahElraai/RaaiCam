# RaaiCam Protocol v1

All integers **big-endian**. Frame: `[u8 type][u32 length][payload]`, `length` = payload bytes, max **4 MiB**; larger → protocol error, close.

| type | name | dir | payload |
|---|---|---|---|
| 0x01 | HELLO | both | UTF-8 JSON `{"proto":1,"role":"phone"\|"pc","app":"RaaiCam","version":"x.y.z","device":"…","os":"…"}` — first frame each way; proto ≠ 1 → close |
| 0x02 | AUTH | — | reserved for 1C (receiving it in 1B → ignore) |
| 0x03 | CMD | pc→phone | UTF-8 JSON, see commands |
| 0x04 | TELEMETRY | phone→pc | UTF-8 JSON every 2 s |
| 0x05 | LOG | phone→pc | UTF-8 text line (no newline), level prefix `I `/`W `/`E ` |
| 0x06 | VIDEO | phone→pc | `[u64 captureUs][u32 seq][u8 flags][Annex-B access unit]`, flags bit0 = keyframe (contains SPS+PPS+IDR) |
| 0x07 | PING | pc→phone | `[u64 pcSendUs]` |
| 0x08 | PONG | phone→pc | `[u64 pcSendUs][u64 phoneRecvUs]` |

Commands (`"cmd"` field): `{"cmd":"start","width":1920,"height":1080,"fps":30,"lens":"wide","bitrateKbps":8000}` · `{"cmd":"stop"}` · `{"cmd":"keyframe"}` · `{"cmd":"bitrate","kbps":6000}` · `{"cmd":"lens","lens":"wide"|"ultra"}`. Unknown cmd → phone replies LOG `W unknown cmd <name>`.

Telemetry: `{"thermal":"nominal"|"fair"|"serious"|"critical","battery":0.0-1.0,"charging":bool,"streaming":bool,"width":int,"height":int,"fpsCapture":float,"fpsEncode":float,"bitrateKbps":int,"lens":"wide"|"ultra","paused":bool}`.

Clocks: `captureUs`/`phoneRecvUs` = phone `mach_continuous_time` in µs; `pcSendUs` = PC `Stopwatch` µs. Offset estimate per PONG = `phoneRecvUs − (pcSendUs + rtt/2)`; keep the sample with the smallest RTT in the last 10 s.

`protocol/test-vectors.json`: hex encodings of one frame of every type (incl. a VIDEO with a 16-byte dummy AU) shared by the C# and Swift tests.

## Versioning

The protocol version is the `proto` integer in HELLO. Version 1 is described here. Any incompatible change (frame layout, type meanings, required fields) bumps `proto`; a peer that receives a HELLO with an unsupported `proto` closes the connection. Additive changes (new optional JSON fields, new commands) do not bump `proto`: receivers ignore unknown JSON fields, and the phone answers unknown commands with a LOG warning.

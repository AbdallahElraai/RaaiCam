import Foundation

// RaaiCam protocol v2 (see protocol/PROTOCOL.md). All integers big-endian.

enum FrameType: UInt8 {
    case hello = 1, auth = 2, cmd = 3, telemetry = 4, log = 5, video = 6, ping = 7, pong = 8, sealed = 9, info = 10
}

enum ProtocolError: Error, Equatable {
    case oversize(Int)
    case badJSON
    case badProto(Int)
}

struct Frame: Equatable {
    let type: UInt8
    let payload: Data
}

enum FrameCodec {
    static let maxPayload = 4 * 1024 * 1024

    static func encode(type: UInt8, payload: Data) -> Data {
        var out = Data(capacity: 5 + payload.count)
        out.append(type)
        out.appendBE(UInt32(payload.count))
        out.append(payload)
        return out
    }

    static func encode(_ type: FrameType, _ payload: Data) -> Data {
        encode(type: type.rawValue, payload: payload)
    }
}

/// Incremental frame reassembler. Throws on a length above the 4 MiB cap
/// as soon as the 5-byte header is complete (before any payload is buffered).
struct FrameDecoder {
    private var buf = [UInt8]()

    mutating func feed(_ data: Data) throws -> [Frame] {
        buf.append(contentsOf: data)
        var out = [Frame]()
        var pos = 0
        while buf.count - pos >= 5 {
            let len = Int(buf[pos + 1]) << 24 | Int(buf[pos + 2]) << 16 | Int(buf[pos + 3]) << 8 | Int(buf[pos + 4])
            if len > FrameCodec.maxPayload { throw ProtocolError.oversize(len) }
            if buf.count - pos - 5 < len { break }
            let start = pos + 5
            out.append(Frame(type: buf[pos], payload: Data(buf[start..<(start + len)])))
            pos = start + len
        }
        if pos > 0 { buf.removeFirst(pos) }
        return out
    }
}

extension Data {
    mutating func appendBE(_ v: UInt32) {
        append(contentsOf: [UInt8(v >> 24 & 0xff), UInt8(v >> 16 & 0xff), UInt8(v >> 8 & 0xff), UInt8(v & 0xff)])
    }
    mutating func appendBE(_ v: UInt64) {
        for shift in stride(from: 56, through: 0, by: -8) { append(UInt8(v >> UInt64(shift) & 0xff)) }
    }
    func readBE64(at offset: Int) -> UInt64? {
        guard count >= offset + 8 else { return nil }
        var v: UInt64 = 0
        for i in 0..<8 { v = v << 8 | UInt64(self[startIndex + offset + i]) }
        return v
    }
}

enum Payloads {
    static func video(captureUs: UInt64, seq: UInt32, keyframe: Bool, accessUnit: Data) -> Data {
        var d = Data(capacity: 13 + accessUnit.count)
        d.appendBE(captureUs)
        d.appendBE(seq)
        d.append(keyframe ? 1 : 0)
        d.append(accessUnit)
        return d
    }

    static func parsePing(_ payload: Data) -> UInt64? { payload.readBE64(at: 0) }

    static func pong(pcSendUs: UInt64, phoneRecvUs: UInt64) -> Data {
        var d = Data(capacity: 16)
        d.appendBE(pcSendUs)
        d.appendBE(phoneRecvUs)
        return d
    }
}

enum Lens: String {
    case wide, ultra
}

struct StartCommand: Equatable {
    var width: Int
    var height: Int
    var fps: Int
    var lens: Lens
    var bitrateKbps: Int
}

enum Command: Equatable {
    case start(StartCommand)
    case stop
    case keyframe
    case bitrate(kbps: Int)
    case lens(Lens)
    case route(video: Bool)
    case unpair
    case unknown(String)

    static func parse(_ data: Data) throws -> Command {
        guard let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let name = obj["cmd"] as? String else { throw ProtocolError.badJSON }
        switch name {
        case "start":
            guard let w = obj["width"] as? Int, let h = obj["height"] as? Int else { throw ProtocolError.badJSON }
            let lens = (obj["lens"] as? String).flatMap(Lens.init(rawValue:)) ?? .wide
            return .start(StartCommand(width: w, height: h, fps: obj["fps"] as? Int ?? 30,
                                       lens: lens, bitrateKbps: obj["bitrateKbps"] as? Int ?? 8000))
        case "stop": return .stop
        case "keyframe": return .keyframe
        case "bitrate":
            guard let k = obj["kbps"] as? Int else { throw ProtocolError.badJSON }
            return .bitrate(kbps: k)
        case "lens":
            guard let l = (obj["lens"] as? String).flatMap(Lens.init(rawValue:)) else { throw ProtocolError.badJSON }
            return .lens(l)
        case "route":
            guard let v = obj["video"] as? Bool else { throw ProtocolError.badJSON }
            return .route(video: v)
        case "unpair": return .unpair
        default: return .unknown(name)
        }
    }
}

enum Hello {
    static let protocolVersion = 2

    /// Plaintext HELLO: no device or OS names (they travel in the sealed INFO frame after AUTH).
    static func phone() -> Data {
        let obj: [String: Any] = ["proto": protocolVersion, "role": "phone"]
        return (try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys])) ?? Data()
    }

    static func info(version: String, device: String, os: String) -> Data {
        let obj: [String: Any] = ["app": "RaaiCam", "version": version, "device": device, "os": os]
        return (try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys])) ?? Data()
    }

    static func parse(_ data: Data) throws -> (proto: Int, role: String) {
        guard let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let proto = obj["proto"] as? Int else { throw ProtocolError.badJSON }
        return (proto, obj["role"] as? String ?? "")
    }
}

struct TelemetrySnapshot: Equatable {
    var thermal: String = "nominal"
    var battery: Double = 0
    var charging: Bool = false
    var streaming: Bool = false
    var width: Int = 0
    var height: Int = 0
    var fpsCapture: Double = 0
    var fpsEncode: Double = 0
    var bitrateKbps: Int = 0
    var lens: Lens = .wide
    var paused: Bool = false

    static func thermalString(_ s: ProcessInfo.ThermalState) -> String {
        switch s {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "nominal"
        }
    }

    func jsonData(link: LinkKind = .usb) -> Data {
        let obj: [String: Any] = [
            "thermal": thermal, "battery": battery, "charging": charging, "streaming": streaming,
            "width": width, "height": height, "fpsCapture": fpsCapture, "fpsEncode": fpsEncode,
            "bitrateKbps": bitrateKbps, "lens": lens.rawValue, "paused": paused, "link": link.rawValue,
        ]
        return (try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys])) ?? Data()
    }
}

enum SessionState: Equatable {
    case waiting
    case connected
    case streaming(width: Int, height: Int)
    case error(String)
}

enum PeerAddress {
    static func isLoopback(v4: UInt32) -> Bool { v4 == 0x7F00_0001 }

    /// `::1` or `::ffff:127.0.0.1`
    static func isLoopback(v6 b: [UInt8]) -> Bool {
        guard b.count == 16 else { return false }
        if b[0..<15].allSatisfy({ $0 == 0 }) && b[15] == 1 { return true }
        let mappedPrefix = b[0..<10].allSatisfy { $0 == 0 } && b[10] == 0xff && b[11] == 0xff
        return mappedPrefix && b[12] == 127 && b[13] == 0 && b[14] == 0 && b[15] == 1
    }
}

extension PeerAddress {
    /// Loopback peers are allowed; so is a peer whose address equals the socket's own local
    /// address (the device connecting to itself cannot originate from the network).
    static func isAllowed(family: Int32, peer: [UInt8], local: [UInt8]) -> Bool {
        if family == AF_INET {
            guard peer.count == 4 else { return false }
            let v = peer.reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
            return isLoopback(v4: v) || peer == local
        }
        if family == AF_INET6 {
            return isLoopback(v6: peer) || (peer.count == 16 && peer == local)
        }
        return false
    }

    static func describe(family: Int32, bytes: [UInt8]) -> String {
        if family == AF_INET && bytes.count == 4 { return bytes.map(String.init).joined(separator: ".") }
        if bytes.count == 16 {
            return stride(from: 0, to: 16, by: 2).map { String(format: "%x", Int(bytes[$0]) << 8 | Int(bytes[$0 + 1])) }
                .joined(separator: ":")
        }
        return "unknown"
    }
}

extension StartCommand {
    static let supportedSizes: [(Int, Int)] = [(1920, 1080), (1280, 720)]
    static let kbpsRange = 500...20000

    static func clampKbps(_ k: Int) -> Int { min(max(k, kbpsRange.lowerBound), kbpsRange.upperBound) }

    /// Validates untrusted PC input: size must be one of the two supported sizes (else 1920x1080),
    /// fps must be 1...60 (else 30; the capture layer later clamps to the device's real ranges),
    /// bitrate is clamped to 500...20000 kbps. Returns the sanitized command plus warning lines.
    func sanitized() -> (command: StartCommand, warnings: [String]) {
        var c = self
        var w = [String]()
        if !Self.supportedSizes.contains(where: { $0.0 == width && $0.1 == height }) {
            w.append("W unsupported size \(width)x\(height), using 1920x1080")
            c.width = 1920; c.height = 1080
        }
        if !(1...60).contains(fps) {
            w.append("W unsupported fps \(fps), using 30")
            c.fps = 30
        }
        let k = Self.clampKbps(bitrateKbps)
        if k != bitrateKbps { w.append("W bitrate \(bitrateKbps) kbps clamped to \(k)") }
        c.bitrateKbps = k
        return (c, w)
    }
}

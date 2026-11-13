import XCTest
import CryptoKit
@testable import RaaiCam

final class BundleMarker {}

extension Data {
    init(hex: String) {
        let s = hex.filter { !$0.isWhitespace }
        var d = Data()
        var i = s.startIndex
        while i < s.endIndex {
            let j = s.index(i, offsetBy: 2)
            d.append(UInt8(s[i..<j], radix: 16)!)
            i = j
        }
        self = d
    }

    func base64URLString() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
}

/// Shared protocol/test-vectors-v2.json (same file the C# tests use). TEST-ONLY keys.
struct V2Vectors {
    struct Mode { let auth1, auth2, auth3, kI2R, kR2I: Data }
    let sIPriv, sIPub, eIPriv, eIPub, sRPriv, sRPub, eRPriv, eRPub, pcId, token: Data
    let paired: Mode
    let pairing: Mode
    let sealedPayload, sealedFrame, highI2R, highR2I: Data

    static func load() throws -> V2Vectors {
        let url = try XCTUnwrap(Bundle(for: BundleMarker.self).url(forResource: "test-vectors-v2", withExtension: "json"))
        let obj = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        func h(_ d: [String: Any], _ k: String) -> Data { Data(hex: d[k] as! String) }
        let keys = obj["keys"] as! [String: Any]
        func mode(_ name: String) -> Mode {
            let m = obj[name] as! [String: Any]
            return Mode(auth1: h(m, "auth1"), auth2: h(m, "auth2"), auth3: h(m, "auth3"), kI2R: h(m, "kI2R"), kR2I: h(m, "kR2I"))
        }
        let sealed = obj["sealed"] as! [String: Any]
        let hi = obj["sealedHighCounterI2R"] as! [String: Any]
        let hr = obj["sealedHighCounterR2I"] as! [String: Any]
        return V2Vectors(sIPriv: h(keys, "sIPriv"), sIPub: h(keys, "sIPub"), eIPriv: h(keys, "eIPriv"), eIPub: h(keys, "eIPub"),
                         sRPriv: h(keys, "sRPriv"), sRPub: h(keys, "sRPub"), eRPriv: h(keys, "eRPriv"), eRPub: h(keys, "eRPub"),
                         pcId: h(keys, "pcId"), token: h(keys, "token"),
                         paired: mode("pairedMode"), pairing: mode("pairingMode"),
                         sealedPayload: h(sealed, "payloadHex"), sealedFrame: h(sealed, "frameHex"),
                         highI2R: h(hi, "payloadHex"), highR2I: h(hr, "payloadHex"))
    }
}

/// Test-only initiator (the PC side), mirroring the C# InitiatorHandshake.
final class TestInitiator {
    let sI: Curve25519.KeyAgreement.PrivateKey
    let eI: Curve25519.KeyAgreement.PrivateKey
    let pcId: Data
    let pairedPhonePub: Data?
    let token: Data?
    private var auth1 = Data()
    private var th2 = Data()
    private var dhEE = Data(), dhSE = Data(), dhES = Data(), dhSS = Data()
    private(set) var keys: SessionKeys?

    init(sI: Curve25519.KeyAgreement.PrivateKey, eI: Curve25519.KeyAgreement.PrivateKey = Curve25519.KeyAgreement.PrivateKey(),
         pcId: Data, pairedPhonePub: Data?, token: Data?) {
        self.sI = sI; self.eI = eI; self.pcId = pcId; self.pairedPhonePub = pairedPhonePub; self.token = token
    }

    var sIPub: Data { sI.publicKey.rawRepresentation }

    func createAuth1() -> Data {
        let ePub = eI.publicKey.rawRepresentation
        var hmac = Data()
        if let t = token { hmac = PairingTokenCheck.computeHmac(token: t, ePub: ePub, sPub: sIPub) }
        auth1 = Kdf.cat(Data([token != nil ? 2 : 1]), ePub, sIPub, pcId, hmac)
        return auth1
    }

    func readAuth2(_ auth2: Data) throws -> Data {
        guard auth2.count == 80 else { throw CryptoProtocolError.failed }
        let eR = auth2.sub(0..<32), ct = auth2.sub(32..<80)
        let th0 = Kdf.sha256(Kdf.th0Label, auth1)
        let th1 = Kdf.sha256(th0, eR)
        dhEE = try Kdf.agree(eI, eR)
        dhSE = try Kdf.agree(sI, eR)
        let k0 = Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, token ?? Data()), label: "auth2", th: th1, length: 32)
        let sR = try Kdf.openOnce(key: k0, ct)
        guard sR.count == 32 else { throw CryptoProtocolError.failed }
        if let p = pairedPhonePub, !Kdf.constantTimeEqual(sR, p) { throw CryptoProtocolError.failed }
        th2 = Kdf.sha256(th1, ct)
        dhES = try Kdf.agree(eI, sR)
        dhSS = try Kdf.agree(sI, sR)
        return sR
    }

    func createAuth3() throws -> Data {
        let psk = token ?? Data()
        let k1 = Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, dhES, psk), label: "auth3", th: th2, length: 32)
        let a3 = try Kdf.sealOnce(key: k1, plaintext: Data("ok".utf8))
        keys = SessionKeys(okm: Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, dhES, dhSS, psk), label: "session", th: th2, length: 64))
        return a3
    }
}

/// A PC-side client that completes hello + handshake against a FrameServer and then speaks SEALED frames.
final class AuthedClient {
    let raw: TestClient
    let channel: SealedChannel

    init(raw: TestClient, channel: SealedChannel) { self.raw = raw; self.channel = channel }

    static let helloPC = FrameCodec.encode(.hello, Data(#"{"proto":2,"role":"pc"}"#.utf8))

    static func connect(port: UInt16, initiator: TestInitiator) throws -> AuthedClient {
        let raw = try XCTUnwrap(TestClient(port: port))
        raw.send(helloPC)
        let hello = raw.readFrames(count: 1)
        XCTAssertEqual(hello.first?.type, FrameType.hello.rawValue)
        raw.send(FrameCodec.encode(.auth, initiator.createAuth1()))
        let a2 = raw.readFrames(count: 1)
        guard let f2 = a2.first, f2.type == FrameType.auth.rawValue else {
            raw.close()
            throw NSError(domain: "handshake", code: 2)
        }
        _ = try initiator.readAuth2(f2.payload)
        raw.send(FrameCodec.encode(.auth, try initiator.createAuth3()))
        let channel = SealedChannel(keys: try XCTUnwrap(initiator.keys), role: .initiator)
        let client = AuthedClient(raw: raw, channel: channel)
        let first = client.readInner(count: 1)
        XCTAssertEqual(first.first?.type, FrameType.info.rawValue, "first sealed frame must be INFO")
        return client
    }

    func sendInner(_ type: FrameType, _ payload: Data, dribble: Bool = false) throws {
        let frame = FrameCodec.encode(.sealed, try channel.seal(innerType: type.rawValue, payload: payload))
        if dribble {
            for b in frame { raw.send(Data([b])); Thread.sleep(forTimeInterval: 0.002) }
        } else {
            raw.send(frame)
        }
    }

    func readInner(count: Int, timeout: TimeInterval = 3) -> [Frame] {
        raw.setTimeout(timeout)
        var out = [Frame]()
        for f in raw.readFrames(count: count) {
            guard f.type == FrameType.sealed.rawValue, let inner = try? channel.open(f.payload) else {
                XCTFail("expected a valid SEALED frame, got type \(f.type)")
                break
            }
            out.append(inner)
        }
        return out
    }

    func close() { raw.close() }
}

/// A store with a paired PC (or none), plus the PC-side keys to talk to it.
struct Fixture {
    let store: PairingStore
    let pcKey = Curve25519.KeyAgreement.PrivateKey()
    let pcId = Data(repeating: 7, count: 16)
    let phonePub: Data

    init(paired: Bool) throws {
        let s = PairingStore(backing: MemorySecretStore())
        store = s
        if paired {
            phonePub = try s.staticKey().publicKey.rawRepresentation
            try s.setPaired(PairedPC(pcId: pcId, pcPub: pcKey.publicKey.rawRepresentation, label: "Test PC"))
        } else {
            phonePub = Data()
        }
    }

    func initiator() -> TestInitiator {
        TestInitiator(sI: pcKey, pcId: pcId, pairedPhonePub: phonePub, token: nil)
    }
}

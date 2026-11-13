import XCTest
import CryptoKit
@testable import RaaiCam

final class HandshakeTests: XCTestCase {
    private func makeCheck(_ v: V2Vectors, expiresIn: TimeInterval = 300, now: @escaping () -> Date = Date.init,
                           markUsed: @escaping () -> Bool = { true }) -> PairingTokenCheck {
        PairingTokenCheck(token: v.token, expiresAt: Date().addingTimeInterval(expiresIn), expectedPcPub: v.sIPub,
                          markUsed: markUsed, now: now)
    }

    private func responder(_ v: V2Vectors, check: PairingTokenCheck?, fixedEphemeral: Bool = true) throws -> ResponderHandshake {
        let sR = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.sRPriv)
        let e: Curve25519.KeyAgreement.PrivateKey? = fixedEphemeral
            ? try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.eRPriv) : nil
        return ResponderHandshake(phoneStatic: sR, isPairedPc: { Kdf.constantTimeEqual($0, v.sIPub) },
                                  tokenCheck: check, ephemeral: e)
    }

    private func initiator(_ v: V2Vectors, pairing: Bool) throws -> TestInitiator {
        TestInitiator(sI: try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.sIPriv),
                      eI: try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.eIPriv),
                      pcId: v.pcId, pairedPhonePub: pairing ? nil : v.sRPub, token: pairing ? v.token : nil)
    }

    func testHandshakeVectors() throws {
        let v = try V2Vectors.load()
        XCTAssertEqual(try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.sIPriv).publicKey.rawRepresentation, v.sIPub)
        XCTAssertEqual(try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: v.eRPriv).publicKey.rawRepresentation, v.eRPub)
        for pairing in [false, true] {
            let m = pairing ? v.pairing : v.paired
            let ini = try initiator(v, pairing: pairing)
            XCTAssertEqual(ini.createAuth1(), m.auth1, "auth1 pairing=\(pairing)")
            let r = try responder(v, check: pairing ? makeCheck(v) : nil)
            let auth2 = try r.readAuth1AndCreateAuth2(m.auth1)
            XCTAssertEqual(auth2, m.auth2, "auth2 pairing=\(pairing)")
            XCTAssertEqual(try ini.readAuth2(auth2), v.sRPub)
            let auth3 = try ini.createAuth3()
            XCTAssertEqual(auth3, m.auth3, "auth3 pairing=\(pairing)")
            try r.readAuth3(m.auth3)
            let res = try XCTUnwrap(r.result)
            XCTAssertEqual(res.keys.i2rData, m.kI2R, "kI2R pairing=\(pairing)")
            XCTAssertEqual(res.keys.r2iData, m.kR2I, "kR2I pairing=\(pairing)")
            XCTAssertEqual(try XCTUnwrap(ini.keys).i2rData, m.kI2R)
            XCTAssertEqual(try XCTUnwrap(ini.keys).r2iData, m.kR2I)
            XCTAssertEqual(res.peerStatic, v.sIPub)
            XCTAssertEqual(res.peerPcId, v.pcId)
            XCTAssertEqual(res.wasPairing, pairing)
        }
    }

    func testReplayRejected() throws {
        let v = try V2Vectors.load()
        // a genuine run with a random responder ephemeral, recorded
        let ini = try initiator(v, pairing: false)
        let auth1 = ini.createAuth1()
        let r1 = try responder(v, check: nil, fixedEphemeral: false)
        _ = try ini.readAuth2(try r1.readAuth1AndCreateAuth2(auth1))
        let auth3 = try ini.createAuth3()
        try r1.readAuth3(auth3)
        // replay both messages to a fresh responder (new E_R): AUTH3 must not verify
        let r2 = try responder(v, check: nil, fixedEphemeral: false)
        _ = try r2.readAuth1AndCreateAuth2(auth1)
        XCTAssertThrowsError(try r2.readAuth3(auth3))
        XCTAssertNil(r2.result)
        // a failed handshake cannot be resumed
        XCTAssertThrowsError(try r2.readAuth3(auth3))
    }

    func testReplayedSealedFrameRejected() throws {
        let v = try V2Vectors.load()
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        let rx = SealedChannel(keys: keys, role: .responder)
        let tx = SealedChannel(keys: keys, role: .initiator)
        let p0 = try tx.seal(innerType: FrameType.cmd.rawValue, payload: Data("{}".utf8))
        let p1 = try tx.seal(innerType: FrameType.cmd.rawValue, payload: Data("{}".utf8))
        _ = try rx.open(p0)
        XCTAssertThrowsError(try rx.open(p0)) // replay (and the channel is now dead)
        XCTAssertThrowsError(try rx.open(p1))
        // a skipped counter is rejected too
        let rx2 = SealedChannel(keys: keys, role: .responder)
        XCTAssertThrowsError(try rx2.open(p1))
    }

    func testExpiredTokenRejected() throws {
        let v = try V2Vectors.load()
        // already expired at AUTH1
        let late = makeCheck(v, expiresIn: 300, now: { Date().addingTimeInterval(301) })
        let r = try responder(v, check: late)
        XCTAssertThrowsError(try r.readAuth1AndCreateAuth2(v.pairing.auth1))
        // expires between AUTH1 and AUTH3: not consumed
        let clock = MutableClock()
        let used = Counter()
        let check = makeCheck(v, expiresIn: 300, now: { clock.now }, markUsed: { used.bump(); return true })
        let r2 = try responder(v, check: check)
        _ = try r2.readAuth1AndCreateAuth2(v.pairing.auth1)
        clock.now = Date().addingTimeInterval(301)
        XCTAssertThrowsError(try r2.readAuth3(v.pairing.auth3))
        XCTAssertEqual(used.value, 0)
    }

    func testTokenSingleUse() throws {
        let v = try V2Vectors.load()
        let used = Counter()
        let check = makeCheck(v, markUsed: { used.bump(); return true })
        let r1 = try responder(v, check: check)
        _ = try r1.readAuth1AndCreateAuth2(v.pairing.auth1)
        try r1.readAuth3(v.pairing.auth3)
        XCTAssertEqual(used.value, 1)
        // the same token never works again
        let r2 = try responder(v, check: check)
        XCTAssertThrowsError(try r2.readAuth1AndCreateAuth2(v.pairing.auth1))
        XCTAssertEqual(used.value, 1)

        // one in-flight claim at a time; released when the first handshake is abandoned
        let check2 = makeCheck(v)
        let a = try responder(v, check: check2)
        _ = try a.readAuth1AndCreateAuth2(v.pairing.auth1)
        let b = try responder(v, check: check2)
        XCTAssertThrowsError(try b.readAuth1AndCreateAuth2(v.pairing.auth1))
        a.dispose()
        let c = try responder(v, check: check2)
        XCTAssertNoThrow(try c.readAuth1AndCreateAuth2(v.pairing.auth1))
    }

    func testTamperRejected() throws {
        let v = try V2Vectors.load()
        // AUTH3 tampered
        let r = try responder(v, check: nil)
        _ = try r.readAuth1AndCreateAuth2(v.paired.auth1)
        var bad3 = v.paired.auth3
        bad3[0] ^= 0x01
        XCTAssertThrowsError(try r.readAuth3(bad3))
        // AUTH1 pairing HMAC tampered
        var badHmac = v.pairing.auth1
        badHmac[badHmac.count - 1] ^= 0x01
        XCTAssertThrowsError(try responder(v, check: makeCheck(v)).readAuth1AndCreateAuth2(badHmac))
        // wrong static key in AUTH1 (not the paired PC)
        var wrongPk = v.paired.auth1
        wrongPk[40] ^= 0x01
        XCTAssertThrowsError(try responder(v, check: nil).readAuth1AndCreateAuth2(wrongPk))
        // mode downgrade
        var downgraded = v.pairing.auth1
        downgraded[0] = 1
        XCTAssertThrowsError(try responder(v, check: makeCheck(v)).readAuth1AndCreateAuth2(downgraded))
        // all-zero E_I (low-order point) is rejected
        var zeroE = v.paired.auth1
        for i in 1..<33 { zeroE[i] = 0 }
        XCTAssertThrowsError(try responder(v, check: nil).readAuth1AndCreateAuth2(zeroE))
        // a sealed frame with one flipped bit closes the channel for good
        let keys = SessionKeys(i2r: v.paired.kI2R, r2i: v.paired.kR2I)
        let rx = SealedChannel(keys: keys, role: .responder)
        var tampered = v.sealedPayload
        tampered[tampered.count - 1] ^= 0x01
        XCTAssertThrowsError(try rx.open(tampered))
        XCTAssertThrowsError(try rx.open(v.sealedPayload))
    }
}

final class MutableClock {
    private let lock = NSLock()
    private var value = Date()
    var now: Date {
        get { lock.lock(); defer { lock.unlock() }; return value }
        set { lock.lock(); value = newValue; lock.unlock() }
    }
}

final class Counter {
    private let lock = NSLock()
    private var n = 0
    func bump() { lock.lock(); n += 1; lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return n }
}

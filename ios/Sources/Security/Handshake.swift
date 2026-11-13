import Foundation
import CryptoKit

struct HandshakeResult {
    let keys: SessionKeys
    let peerStatic: Data
    let peerPcId: Data
    let wasPairing: Bool
}

/// Responder (phone) side of the protocol v2 handshake. One instance per connection, used from that
/// connection's reader thread only. Mirrors the C# `ResponderHandshake`.
final class ResponderHandshake {
    private enum State { case new, sent2, done, failed }

    private let sR: Curve25519.KeyAgreement.PrivateKey
    private let eR: Curve25519.KeyAgreement.PrivateKey
    private let isPairedPc: (Data) -> Bool
    private let tokenCheck: PairingTokenCheck?
    private let expectedPairedPcId: Data?
    private var state = State.new
    private var claimedToken = false
    private var wasPairing = false
    private var psk = Data()
    private var sIPub = Data()
    private var pcId = Data()
    private var th2 = Data()
    private var dhEE = Data(), dhSE = Data(), dhES = Data(), dhSS = Data()
    private var keys: SessionKeys?

    /// `ephemeral` is a test seam (fixed vectors); production passes nil for a fresh key.
    init(phoneStatic: Curve25519.KeyAgreement.PrivateKey, isPairedPc: @escaping (Data) -> Bool,
         tokenCheck: PairingTokenCheck?, expectedPairedPcId: Data? = nil,
         ephemeral: Curve25519.KeyAgreement.PrivateKey? = nil) {
        self.expectedPairedPcId = expectedPairedPcId
        self.sR = phoneStatic
        self.isPairedPc = isPairedPc
        self.tokenCheck = tokenCheck
        self.eR = ephemeral ?? Curve25519.KeyAgreement.PrivateKey()
    }

    /// Non-nil only after AUTH3 verified.
    var result: HandshakeResult? {
        guard state == .done, let k = keys else { return nil }
        return HandshakeResult(keys: k, peerStatic: sIPub, peerPcId: pcId, wasPairing: wasPairing)
    }

    func readAuth1AndCreateAuth2(_ auth1: Data) throws -> Data {
        do {
            guard state == .new, auth1.count >= 1 else { throw CryptoProtocolError.failed }
            let mode = auth1[auth1.startIndex]
            let pairing = mode == 2
            guard (mode == 1 && auth1.count == 81) || (pairing && auth1.count == 97) else { throw CryptoProtocolError.failed }
            let eI = auth1.sub(1..<33), sI = auth1.sub(33..<65), id = auth1.sub(65..<81)

            if pairing {
                guard let tc = tokenCheck,
                      tc.verifyHmac(ePub: eI, sPub: sI, presented: auth1.sub(81..<97)),
                      tc.tryBegin(sIPub: sI, pcId: id) else { throw CryptoProtocolError.failed }
                claimedToken = true
                psk = tc.token
            } else {
                guard isPairedPc(sI) else { throw CryptoProtocolError.failed }
                if let want = expectedPairedPcId, !Kdf.constantTimeEqual(want, id) { throw CryptoProtocolError.failed }
            }

            let ePub = eR.publicKey.rawRepresentation
            let th0 = Kdf.sha256(Kdf.th0Label, auth1)
            let th1 = Kdf.sha256(th0, ePub)
            dhEE = try Kdf.agree(eR, eI)
            dhSE = try Kdf.agree(eR, sI)
            let k0 = Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, psk), label: "auth2", th: th1, length: 32)
            let ct = try Kdf.sealOnce(key: k0, plaintext: sR.publicKey.rawRepresentation)
            th2 = Kdf.sha256(th1, ct)
            dhES = try Kdf.agree(sR, eI)
            dhSS = try Kdf.agree(sR, sI)
            sIPub = sI; pcId = id; wasPairing = pairing
            state = .sent2
            return Kdf.cat(ePub, ct)
        } catch {
            fail()
            throw CryptoProtocolError.failed
        }
    }

    func readAuth3(_ auth3: Data) throws {
        do {
            guard state == .sent2, auth3.count == 18 else { throw CryptoProtocolError.failed }
            let k1 = Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, dhES, psk), label: "auth3", th: th2, length: 32)
            let ok = try Kdf.openOnce(key: k1, auth3)
            guard Kdf.constantTimeEqual(ok, Data("ok".utf8)) else { throw CryptoProtocolError.failed }
            if wasPairing {
                guard let tc = tokenCheck, tc.consume() else { throw CryptoProtocolError.failed }
                claimedToken = false
            }
            keys = SessionKeys(okm: Kdf.derive(ikm: Kdf.cat(dhEE, dhSE, dhES, dhSS, psk), label: "session", th: th2, length: 64))
            state = .done
            wipeSecrets()
        } catch {
            fail()
            throw CryptoProtocolError.failed
        }
    }

    /// Releases a token claim that was not consumed (connection closed or handshake abandoned).
    func dispose() {
        releaseToken()
        if state != .done { wipeSecrets() }
    }

    private func wipeSecrets() {
        dhEE.wipe(); dhSE.wipe(); dhES.wipe(); dhSS.wipe(); th2.wipe(); psk.wipe()
    }

    private func fail() {
        state = .failed
        wipeSecrets()
        releaseToken()
    }

    private func releaseToken() {
        if claimedToken { tokenCheck?.release(); claimedToken = false }
    }
}

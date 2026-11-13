import Foundation
import CryptoKit

/// Generic failure: the message never carries key material, tokens or peer data.
enum CryptoProtocolError: Error { case failed }

extension Data {
    /// Re-based sub-range (indices start at 0).
    func sub(_ r: Range<Int>) -> Data {
        Data(self[(startIndex + r.lowerBound)..<(startIndex + r.upperBound)])
    }

    /// Best-effort erase (Data is a value type; copies made elsewhere are not reachable).
    mutating func wipe() {
        withUnsafeMutableBytes { raw in
            if let base = raw.baseAddress, raw.count > 0 { _ = memset_s(base, raw.count, 0, raw.count) }
        }
        self = Data()
    }

    /// Decodes unpadded or padded base64url. nil on bad input.
    init?(base64URL s: String) {
        var t = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while t.count % 4 != 0 { t += "=" }
        guard let d = Data(base64Encoded: t) else { return nil }
        self = d
    }
}

/// CryptoKit-only primitives for protocol v2 (see protocol/PROTOCOL.md "Protocol v2 handshake").
enum Kdf {
    static let salt = Data("raaicam-v2".utf8)
    static let th0Label = Data("raaicam-v2".utf8)
    static let tagSize = 16
    static let nonceSize = 12

    static func cat(_ parts: Data...) -> Data {
        var out = Data()
        for p in parts { out.append(p) }
        return out
    }

    static func sha256(_ parts: Data...) -> Data {
        var h = SHA256()
        for p in parts { h.update(data: p) }
        return Data(h.finalize())
    }

    static func bytes(_ key: SymmetricKey) -> Data { key.withUnsafeBytes { Data($0) } }

    /// HKDF-SHA256(ikm, salt = "raaicam-v2", info = label || th, length).
    static func derive(ikm: Data, label: String, th: Data, length: Int) -> SymmetricKey {
        HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: salt,
                               info: cat(Data(label.utf8), th), outputByteCount: length)
    }

    /// Raw X25519 shared secret. An all-zero output (low-order peer point) is a failure.
    static func agree(_ priv: Curve25519.KeyAgreement.PrivateKey, _ peerPub: Data) throws -> Data {
        guard peerPub.count == 32,
              let pub = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: peerPub),
              let secret = try? priv.sharedSecretFromKeyAgreement(with: pub) else { throw CryptoProtocolError.failed }
        var d = secret.withUnsafeBytes { Data($0) }
        var acc: UInt8 = 0
        for b in d { acc |= b }
        if d.count != 32 || acc == 0 { d.wipe(); throw CryptoProtocolError.failed }
        return d
    }

    /// Constant-time equality over equal-length byte strings (false on length mismatch).
    static func constantTimeEqual(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        let x = [UInt8](a), y = [UInt8](b)
        for i in 0..<x.count { diff |= x[i] ^ y[i] }
        return diff == 0
    }

    /// AUTH2/AUTH3 AEAD: 12 zero-byte nonce, empty AAD, one message per key. Returns ciphertext || tag.
    static func sealOnce(key: SymmetricKey, plaintext: Data) throws -> Data {
        do {
            let nonce = try AES.GCM.Nonce(data: Data(count: nonceSize))
            let box = try AES.GCM.seal(plaintext, using: key, nonce: nonce)
            return box.ciphertext + box.tag
        } catch { throw CryptoProtocolError.failed }
    }

    static func openOnce(key: SymmetricKey, _ ciphertextAndTag: Data) throws -> Data {
        guard ciphertextAndTag.count >= tagSize else { throw CryptoProtocolError.failed }
        do {
            let n = ciphertextAndTag.count - tagSize
            let nonce = try AES.GCM.Nonce(data: Data(count: nonceSize))
            let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertextAndTag.sub(0..<n),
                                            tag: ciphertextAndTag.sub(n..<ciphertextAndTag.count))
            return try AES.GCM.open(box, using: key)
        } catch { throw CryptoProtocolError.failed }
    }
}

/// The two directional AES-256 keys of one connection. SymmetricKey storage is zeroed by CryptoKit on release.
final class SessionKeys {
    let i2r: SymmetricKey
    let r2i: SymmetricKey

    init(i2r: SymmetricKey, r2i: SymmetricKey) { self.i2r = i2r; self.r2i = r2i }

    /// okm = k_i2r (32) || k_r2i (32)
    convenience init(okm: SymmetricKey) {
        var raw = Kdf.bytes(okm)
        self.init(i2r: SymmetricKey(data: raw.sub(0..<32)), r2i: SymmetricKey(data: raw.sub(32..<64)))
        raw.wipe()
    }

    /// Test helper: build from raw 32-byte keys.
    convenience init(i2r: Data, r2i: Data) {
        self.init(i2r: SymmetricKey(data: i2r), r2i: SymmetricKey(data: r2i))
    }

    var i2rData: Data { Kdf.bytes(i2r) }
    var r2iData: Data { Kdf.bytes(r2i) }
}

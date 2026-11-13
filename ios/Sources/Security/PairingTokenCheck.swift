import Foundation
import CryptoKit

/// The one-time pairing token scanned from the PC's QR code (memory only; never persisted or logged).
struct PairingToken: Equatable {
    var token: Data      // 16 bytes
    var pcId: Data       // 16 bytes
    var pcPub: Data      // 32 bytes, the PC's static X25519 key from the QR
    var name: String
    var expiresAt: Date
}

/// Responder-side token state: HMAC check, expiry, `S_I.pub == pk` check, one in-flight claim at a time,
/// and consume-only-after-AUTH3 (see protocol "Responder checks").
final class PairingTokenCheck {
    static let hmacSize = 16
    private static let domain = Data("raaicam-pair-v2".utf8)

    private let tokenBytes: Data
    private let expiresAt: Date
    private let expectedPcPub: Data
    private let markUsed: () -> Bool
    private let now: () -> Date
    private let lock = NSLock()
    private var used = false
    private var inFlight = false
    private var consuming = false

    init(token: Data, expiresAt: Date, expectedPcPub: Data, markUsed: @escaping () -> Bool,
         now: @escaping () -> Date = Date.init) {
        self.tokenBytes = token
        self.expiresAt = expiresAt
        self.expectedPcPub = expectedPcPub
        self.markUsed = markUsed
        self.now = now
    }

    var token: Data { tokenBytes }

    /// HMAC-SHA256(token, "raaicam-pair-v2" || E_I.pub || S_I.pub) truncated to 16 bytes.
    static func computeHmac(token: Data, ePub: Data, sPub: Data) -> Data {
        let mac = HMAC<SHA256>.authenticationCode(for: Kdf.cat(domain, ePub, sPub), using: SymmetricKey(data: token))
        return Data(mac).sub(0..<hmacSize)
    }

    func verifyHmac(ePub: Data, sPub: Data, presented: Data) -> Bool {
        Kdf.constantTimeEqual(Self.computeHmac(token: tokenBytes, ePub: ePub, sPub: sPub), presented)
    }

    /// Claims the token for one handshake. False if used, already claimed, expired or `S_I.pub` != QR `pk`.
    func tryBegin(sIPub: Data) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if used || inFlight { return false }
        if now() >= expiresAt { return false }
        if !Kdf.constantTimeEqual(expectedPcPub, sIPub) { return false }
        inFlight = true
        return true
    }

    func release() {
        lock.lock(); inFlight = false; lock.unlock()
    }

    /// Called after AUTH3 verified. Re-checks expiry; the owner callback (which persists the pairing)
    /// runs outside the lock.
    func consume() -> Bool {
        lock.lock()
        if used || !inFlight || consuming || now() >= expiresAt { lock.unlock(); return false }
        consuming = true
        lock.unlock()
        let ok = markUsed()
        lock.lock()
        consuming = false
        if ok { used = true; inFlight = false }
        lock.unlock()
        return ok
    }
}

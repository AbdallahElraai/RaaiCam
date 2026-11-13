import Foundation
import CryptoKit

/// SEALED frame codec for one connection (protocol v2, "AEAD rules").
/// Payload layout: `[u64 counter][ciphertext || 16-byte tag]`, plaintext = one inner frame
/// `[u8 type][u32 len][payload]`. Nonce = `[u32 BE direction][u64 BE counter]`, AAD = `[0x09][u64 BE counter]`.
/// Any failure on `open` kills the channel for good.
final class SealedChannel {
    enum Role { case initiator, responder }

    static let sealedType: UInt8 = 0x09
    /// CMD, TELEMETRY, LOG, VIDEO, PING, PONG, INFO
    static let allowedInnerTypes: Set<UInt8> = [3, 4, 5, 6, 7, 8, 10]

    private let sendKey: SymmetricKey
    private let recvKey: SymmetricKey
    private let sendDirection: UInt32
    private let recvDirection: UInt32
    private let sendLock = NSLock()
    private let recvLock = NSLock()
    private var sendCounter: UInt64
    private var recvCounter: UInt64
    private var dead = false
    private var confirmed = false

    init(keys: SessionKeys, role: Role, sendCounter: UInt64 = 0, recvCounter: UInt64 = 0) {
        switch role {
        case .initiator:
            sendKey = keys.i2r; recvKey = keys.r2i; sendDirection = 1; recvDirection = 2
        case .responder:
            sendKey = keys.r2i; recvKey = keys.i2r; sendDirection = 2; recvDirection = 1
        }
        self.sendCounter = sendCounter
        self.recvCounter = recvCounter
    }

    /// True after the first successful `open` (key confirmation for the initiator).
    var isConfirmed: Bool { recvLock.lock(); defer { recvLock.unlock() }; return confirmed }

    private static func nonce(direction: UInt32, counter: UInt64) throws -> AES.GCM.Nonce {
        var n = Data(capacity: 12)
        n.appendBE(direction)
        n.appendBE(counter)
        return try AES.GCM.Nonce(data: n)
    }

    private static func aad(counter: UInt64) -> Data {
        var a = Data(capacity: 9)
        a.append(sealedType)
        a.appendBE(counter)
        return a
    }

    /// Encrypts one inner frame. Callers must write the result to the socket under the same lock
    /// (FrameServer does both on its serial write queue) so frames hit the wire in counter order.
    func seal(innerType: UInt8, payload: Data) throws -> Data {
        var inner = Data(capacity: 5 + payload.count)
        inner.append(innerType)
        inner.appendBE(UInt32(payload.count))
        inner.append(payload)
        return try sealRaw(plaintext: inner)
    }

    /// Seals an arbitrary plaintext (test seam for malformed inner frames).
    func sealRaw(plaintext: Data) throws -> Data {
        sendLock.lock(); defer { sendLock.unlock() }
        guard sendCounter < UInt64.max - 1 else { throw CryptoProtocolError.failed }
        let counter = sendCounter
        do {
            let box = try AES.GCM.seal(plaintext, using: sendKey,
                                       nonce: try Self.nonce(direction: sendDirection, counter: counter),
                                       authenticating: Self.aad(counter: counter))
            sendCounter += 1
            var out = Data(capacity: 8 + plaintext.count + Kdf.tagSize)
            out.appendBE(counter)
            out.append(box.ciphertext)
            out.append(box.tag)
            return out
        } catch { throw CryptoProtocolError.failed }
    }

    /// Verifies and decrypts one SEALED payload. Throws (and permanently closes the channel) on any
    /// counter mismatch, tag failure, truncation or malformed/forbidden inner frame.
    func open(_ payload: Data) throws -> Frame {
        recvLock.lock(); defer { recvLock.unlock() }
        if dead { throw CryptoProtocolError.failed }
        do {
            guard payload.count >= 8 + Kdf.tagSize + 5, let counter = payload.readBE64(at: 0),
                  counter == recvCounter else { throw CryptoProtocolError.failed }
            let n = payload.count - Kdf.tagSize
            let box = try AES.GCM.SealedBox(nonce: try Self.nonce(direction: recvDirection, counter: counter),
                                            ciphertext: payload.sub(8..<n), tag: payload.sub(n..<payload.count))
            let plain = try AES.GCM.open(box, using: recvKey, authenticating: Self.aad(counter: counter))
            guard plain.count >= 5 else { throw CryptoProtocolError.failed }
            let type = plain[plain.startIndex]
            var len = 0
            for i in 1...4 { len = len << 8 | Int(plain[plain.startIndex + i]) }
            guard len == plain.count - 5, Self.allowedInnerTypes.contains(type) else { throw CryptoProtocolError.failed }
            recvCounter += 1
            confirmed = true
            return Frame(type: type, payload: plain.sub(5..<plain.count))
        } catch {
            dead = true
            throw CryptoProtocolError.failed
        }
    }
}

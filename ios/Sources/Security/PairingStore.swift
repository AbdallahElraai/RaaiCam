import Foundation
import CryptoKit
import Security

/// The paired PC as persisted in the Keychain.
struct PairedPC: Equatable, Codable {
    var pcId: Data
    var pcPub: Data
    var label: String
}

/// Secret storage seam (Keychain in the app, memory in tests).
protocol SecretStore: AnyObject {
    func get(_ account: String) -> Data?
    @discardableResult func set(_ account: String, _ data: Data) -> Bool
    func delete(_ account: String)
}

final class MemorySecretStore: SecretStore {
    private let lock = NSLock()
    private var items = [String: Data]()
    func get(_ account: String) -> Data? { lock.lock(); defer { lock.unlock() }; return items[account] }
    func set(_ account: String, _ data: Data) -> Bool { lock.lock(); items[account] = data; lock.unlock(); return true }
    func delete(_ account: String) { lock.lock(); items[account] = nil; lock.unlock() }
}

/// Generic-password Keychain items under one service, readable after the first unlock, never synced or migrated.
final class KeychainSecretStore: SecretStore {
    private let service: String
    init(service: String) { self.service = service }

    private func base(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    func get(_ account: String) -> Data? {
        var q = base(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return nil }
        return out as? Data
    }

    func set(_ account: String, _ data: Data) -> Bool {
        SecItemDelete(base(account) as CFDictionary)
        var q = base(account)
        q[kSecValueData as String] = data
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(q as CFDictionary, nil) == errSecSuccess
    }

    func delete(_ account: String) {
        SecItemDelete(base(account) as CFDictionary)
    }
}

enum PairingStoreError: Error { case storage }

/// What the FrameServer needs from the pairing state.
protocol ServerAuthority: AnyObject {
    /// True while paired or while a pairing token is pending (otherwise nothing but loopback HELLO is served).
    var isPairedOrPending: Bool { get }
    /// A fresh responder for one connection, or nil when nothing can be authenticated.
    func makeResponder() -> ResponderHandshake?
}

/// Pairing state: persistent {pcId, S_I.pub, label} and the phone's static key (Keychain, service
/// `com.raaicam.app.pairing`), plus the in-memory pending token (valid until `exp`).
final class PairingStore: ServerAuthority {
    static let service = "com.raaicam.app.pairing"
    private static let pairedAccount = "paired"
    private static let staticAccount = "static-key"

    private struct Pending {
        let id: UInt64
        let token: PairingToken
        let check: PairingTokenCheck
    }

    private let backing: SecretStore
    private let now: () -> Date
    private let lock = NSLock()
    private var cachedPaired: PairedPC?
    private var pairedLoaded = false
    private var cachedStatic: Curve25519.KeyAgreement.PrivateKey?
    private var staticLoaded = false
    private var pending: Pending?
    private var nextPendingId: UInt64 = 0
    private var observers = [() -> Void]()

    init(backing: SecretStore = KeychainSecretStore(service: PairingStore.service), now: @escaping () -> Date = Date.init) {
        self.backing = backing
        self.now = now
    }

    /// Called (on an arbitrary thread, outside the lock) after every pairing-state change.
    func addObserver(_ f: @escaping () -> Void) {
        lock.lock(); observers.append(f); lock.unlock()
    }

    private func notify() {
        lock.lock(); let list = observers; lock.unlock()
        list.forEach { $0() }
    }

    // MARK: state

    var paired: PairedPC? {
        lock.lock(); defer { lock.unlock() }
        if !pairedLoaded {
            pairedLoaded = true
            if let d = backing.get(Self.pairedAccount) { cachedPaired = try? JSONDecoder().decode(PairedPC.self, from: d) }
        }
        return cachedPaired
    }

    /// The pending token, or nil when none or expired.
    var pendingToken: PairingToken? {
        lock.lock(); defer { lock.unlock() }
        return livePendingLocked()?.token
    }

    private func livePendingLocked() -> Pending? {
        guard let p = pending else { return nil }
        if now() >= p.token.expiresAt { pending = nil; return nil }
        return p
    }

    var isPairedOrPending: Bool { paired != nil || pendingToken != nil }

    // MARK: static key

    private func loadStaticLocked() -> Curve25519.KeyAgreement.PrivateKey? {
        if !staticLoaded {
            staticLoaded = true
            if let d = backing.get(Self.staticAccount) { cachedStatic = try? Curve25519.KeyAgreement.PrivateKey(rawRepresentation: d) }
        }
        return cachedStatic
    }

    /// The phone's long-term X25519 key, generated and persisted on first use.
    func staticKey() throws -> Curve25519.KeyAgreement.PrivateKey {
        lock.lock(); defer { lock.unlock() }
        if let k = loadStaticLocked() { return k }
        let k = Curve25519.KeyAgreement.PrivateKey()
        guard backing.set(Self.staticAccount, k.rawRepresentation) else { throw PairingStoreError.storage }
        cachedStatic = k
        return k
    }

    func existingStaticPublic() -> Data? {
        lock.lock(); defer { lock.unlock() }
        return loadStaticLocked()?.publicKey.rawRepresentation
    }

    // MARK: pairing lifecycle

    /// Remembers a freshly scanned token (replacing any earlier one) and makes sure the phone key exists.
    func beginPairing(_ token: PairingToken) throws {
        _ = try staticKey()
        lock.lock()
        nextPendingId += 1
        let id = nextPendingId
        let check = PairingTokenCheck(token: token.token, expiresAt: token.expiresAt, expectedPcPub: token.pcPub,
                                      markUsed: { [weak self] in self?.completePairing(id: id) ?? false }, now: now)
        pending = Pending(id: id, token: token, check: check)
        lock.unlock()
        let delay = max(0.01, token.expiresAt.timeIntervalSince(now()))
        DispatchQueue.global().asyncAfter(deadline: .now() + delay + 0.05) { [weak self] in self?.expirePending(id: id) }
        notify()
    }

    func cancelPending() {
        lock.lock(); pending = nil; lock.unlock()
        notify()
    }

    private func expirePending(id: UInt64) {
        lock.lock()
        let hit = pending?.id == id
        if hit { pending = nil }
        lock.unlock()
        if hit { notify() }
    }

    /// Runs inside AUTH3 verification of the pairing handshake: persists the PC and consumes the token.
    private func completePairing(id: UInt64) -> Bool {
        lock.lock()
        guard let p = pending, p.id == id else { lock.unlock(); return false }
        lock.unlock()
        let record = PairedPC(pcId: p.token.pcId, pcPub: p.token.pcPub, label: p.token.name)
        do { try setPaired(record) } catch { return false }
        lock.lock()
        if pending?.id == id { pending = nil }
        lock.unlock()
        notify()
        return true
    }

    /// Persists the paired PC (replacing any earlier pairing).
    func setPaired(_ pc: PairedPC) throws {
        guard let data = try? JSONEncoder().encode(pc) else { throw PairingStoreError.storage }
        lock.lock()
        guard backing.set(Self.pairedAccount, data) else { lock.unlock(); throw PairingStoreError.storage }
        cachedPaired = pc
        pairedLoaded = true
        lock.unlock()
        notify()
    }

    /// Wipes the pairing and the phone key from the Keychain and forgets any pending token.
    func unpair() {
        lock.lock()
        backing.delete(Self.pairedAccount)
        backing.delete(Self.staticAccount)
        cachedPaired = nil; pairedLoaded = true
        cachedStatic = nil; staticLoaded = true
        pending = nil
        lock.unlock()
        notify()
    }

    // MARK: ServerAuthority

    func makeResponder() -> ResponderHandshake? {
        lock.lock()
        let pend = livePendingLocked()
        var pc: PairedPC?
        if !pairedLoaded {
            pairedLoaded = true
            if let d = backing.get(Self.pairedAccount) { cachedPaired = try? JSONDecoder().decode(PairedPC.self, from: d) }
        }
        pc = cachedPaired
        let key = loadStaticLocked()
        lock.unlock()
        guard let sR = key, pc != nil || pend != nil else { return nil }
        let pairedKey = pc?.pcPub
        return ResponderHandshake(phoneStatic: sR,
                                  isPairedPc: { peer in pairedKey.map { Kdf.constantTimeEqual(peer, $0) } ?? false },
                                  tokenCheck: pend?.check)
    }
}

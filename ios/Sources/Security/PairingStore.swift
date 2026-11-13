import Foundation
import CryptoKit
import Security

/// The paired PC as persisted in the Keychain.
struct PairedPC: Equatable, Codable {
    var pcId: Data
    var pcPub: Data
    var label: String
}

enum SecretStoreError: Error { case failed(Int32) }

/// Secret storage seam (Keychain in the app, memory in tests).
protocol SecretStore: AnyObject {
    /// nil = the item does not exist; throws on any other failure (never treat that as "absent").
    func get(_ account: String) throws -> Data?
    @discardableResult func set(_ account: String, _ data: Data) -> Bool
    /// True if the item is gone afterwards (including "was not there").
    @discardableResult func delete(_ account: String) -> Bool
}

final class MemorySecretStore: SecretStore {
    private let lock = NSLock()
    private var items = [String: Data]()
    /// Test hooks for Keychain failures.
    var failReads = false
    var failDeletes = false
    func get(_ account: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        if failReads { throw SecretStoreError.failed(-25308) }
        return items[account]
    }
    func set(_ account: String, _ data: Data) -> Bool { lock.lock(); items[account] = data; lock.unlock(); return true }
    func delete(_ account: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if failDeletes { return false }
        items[account] = nil
        return true
    }
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

    func get(_ account: String) throws -> Data? {
        var q = base(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &out)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let d = out as? Data else { throw SecretStoreError.failed(status) }
        return d
    }

    func set(_ account: String, _ data: Data) -> Bool {
        let update = SecItemUpdate(base(account) as CFDictionary,
                                   [kSecValueData as String: data,
                                    kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly] as CFDictionary)
        if update == errSecSuccess { return true }
        guard update == errSecItemNotFound else { return false }
        var q = base(account)
        q[kSecValueData as String] = data
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(q as CFDictionary, nil) == errSecSuccess
    }

    func delete(_ account: String) -> Bool {
        let status = SecItemDelete(base(account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
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

    /// Loads (and caches) the paired record. A Keychain error is thrown and never cached as "unpaired".
    private func loadPairedLocked() throws -> PairedPC? {
        if pairedLoaded { return cachedPaired }
        do {
            guard let d = try backing.get(Self.pairedAccount) else { pairedLoaded = true; cachedPaired = nil; return nil }
            cachedPaired = try JSONDecoder().decode(PairedPC.self, from: d)
            pairedLoaded = true
            return cachedPaired
        } catch { throw PairingStoreError.storage }
    }

    private func loadStaticLocked() throws -> Curve25519.KeyAgreement.PrivateKey? {
        if staticLoaded { return cachedStatic }
        do {
            guard let d = try backing.get(Self.staticAccount) else { staticLoaded = true; cachedStatic = nil; return nil }
            cachedStatic = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: d)
            staticLoaded = true
            return cachedStatic
        } catch { throw PairingStoreError.storage }
    }

    /// The paired PC, or nil when unpaired or when the Keychain cannot be read right now.
    var paired: PairedPC? {
        lock.lock(); defer { lock.unlock() }
        return (try? loadPairedLocked()) ?? nil
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

    /// The phone's long-term X25519 key, generated and persisted on first use. If the Keychain cannot be
    /// read (anything other than "not found"), this throws instead of minting a replacement key.
    func staticKey() throws -> Curve25519.KeyAgreement.PrivateKey {
        lock.lock(); defer { lock.unlock() }
        if let k = try loadStaticLocked() { return k }
        let k = Curve25519.KeyAgreement.PrivateKey()
        guard backing.set(Self.staticAccount, k.rawRepresentation) else { throw PairingStoreError.storage }
        cachedStatic = k
        staticLoaded = true
        return k
    }

    func existingStaticPublic() -> Data? {
        lock.lock(); defer { lock.unlock() }
        return ((try? loadStaticLocked()) ?? nil)?.publicKey.rawRepresentation
    }

    // MARK: pairing lifecycle

    /// Remembers a freshly scanned token (replacing any earlier one) and makes sure the phone key exists.
    func beginPairing(_ token: PairingToken) throws {
        _ = try staticKey()
        lock.lock()
        nextPendingId += 1
        let id = nextPendingId
        let check = PairingTokenCheck(token: token.token, expiresAt: token.expiresAt, expectedPcPub: token.pcPub, expectedPcId: token.pcId,
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

    /// Wipes the pairing and the phone key from the Keychain and forgets any pending token. If a Keychain
    /// delete fails the error is thrown, the in-memory state is dropped and the next access re-reads the
    /// real Keychain contents (so state never claims more or less than what is stored).
    func unpair() throws {
        lock.lock()
        let a = backing.delete(Self.pairedAccount)
        let b = backing.delete(Self.staticAccount)
        pairedLoaded = false; cachedPaired = nil
        staticLoaded = false; cachedStatic = nil
        pending = nil
        lock.unlock()
        notify()
        if !(a && b) { throw PairingStoreError.storage }
    }

    // MARK: ServerAuthority

    func makeResponder() -> ResponderHandshake? {
        lock.lock()
        let pend = livePendingLocked()
        let pcOpt = try? loadPairedLocked()
        let keyOpt = try? loadStaticLocked()
        lock.unlock()
        let pc: PairedPC? = pcOpt ?? nil
        guard let sR = keyOpt ?? nil, pc != nil || pend != nil else { return nil }
        let pairedKey = pc?.pcPub
        return ResponderHandshake(phoneStatic: sR,
                                  isPairedPc: { peer in pairedKey.map { Kdf.constantTimeEqual(peer, $0) } ?? false },
                                  tokenCheck: pend?.check, expectedPairedPcId: pc?.pcId)
    }
}

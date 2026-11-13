import XCTest
import CryptoKit
@testable import RaaiCam

final class PairingTests: XCTestCase {
    private let pcId = Data(repeating: 0xA1, count: 16)
    private let pcPub = Data(repeating: 0xB2, count: 32)
    private let tok = Data(repeating: 0xC3, count: 16)

    private func url(v: String = "2", name: String = "Desk%20PC", exp: Date, pc: Data? = nil, pk: Data? = nil, tok: Data? = nil) -> String {
        "raaicam://pair?v=\(v)&pc=\((pc ?? pcId).base64URLString())&name=\(name)&pk=\((pk ?? pcPub).base64URLString())"
            + "&tok=\((tok ?? self.tok).base64URLString())&exp=\(Int(exp.timeIntervalSince1970))"
    }

    func testPairingURLParses() throws {
        let now = Date()
        let exp = now.addingTimeInterval(300)
        let t = try PairingURL.parse(url(exp: exp), now: now).get()
        XCTAssertEqual(t.pcId, pcId)
        XCTAssertEqual(t.pcPub, pcPub)
        XCTAssertEqual(t.token, tok)
        XCTAssertEqual(t.name, "Desk PC")
        XCTAssertEqual(t.expiresAt.timeIntervalSince1970, Double(Int(exp.timeIntervalSince1970)), accuracy: 1)
        // a far-future exp is clamped to 5 minutes from now
        let far = try PairingURL.parse(url(exp: now.addingTimeInterval(86400)), now: now).get()
        XCTAssertLessThanOrEqual(far.expiresAt.timeIntervalSince(now), 300.5)
        // an empty name gets a default label
        XCTAssertEqual(try PairingURL.parse(url(name: "", exp: exp), now: now).get().name, "your PC")
    }

    func testPairingURLRejectsBadVersion() {
        let now = Date()
        let exp = now.addingTimeInterval(300)
        XCTAssertEqual(PairingURL.parse(url(v: "1", exp: exp), now: now).failureValue, .badVersion)
        XCTAssertEqual(PairingURL.parse(url(v: "3", exp: exp), now: now).failureValue, .badVersion)
        XCTAssertEqual(PairingURL.parse("https://example.com/pair?v=2", now: now).failureValue, .notRaaiCam)
        XCTAssertEqual(PairingURL.parse("hello world", now: now).failureValue, .notRaaiCam)
        // malformed fields
        XCTAssertEqual(PairingURL.parse(url(exp: exp, pk: Data(repeating: 1, count: 31)), now: now).failureValue, .malformed)
        XCTAssertEqual(PairingURL.parse(url(exp: exp, tok: Data(repeating: 1, count: 15)), now: now).failureValue, .malformed)
        XCTAssertEqual(PairingURL.parse(url(exp: exp, pc: Data(repeating: 1, count: 8)), now: now).failureValue, .malformed)
        // expired
        XCTAssertEqual(PairingURL.parse(url(exp: now.addingTimeInterval(-1)), now: now).failureValue, .expired)
    }

    func testStoreLifecycleAndUnpair() throws {
        let mem = MemorySecretStore()
        let store = PairingStore(backing: mem)
        XCTAssertNil(store.paired)
        XCTAssertNil(store.pendingToken)
        XCTAssertFalse(store.isPairedOrPending)
        XCTAssertNil(store.existingStaticPublic())
        XCTAssertNil(store.makeResponder())

        let token = PairingToken(token: tok, pcId: pcId, pcPub: pcPub, name: "Desk PC", expiresAt: Date().addingTimeInterval(300))
        try store.beginPairing(token)
        XCTAssertTrue(store.isPairedOrPending)
        XCTAssertNotNil(store.existingStaticPublic())
        XCTAssertNotNil(store.makeResponder())

        // persisted pairing survives a new store over the same backing; the pending token does not
        try store.setPaired(PairedPC(pcId: pcId, pcPub: pcPub, label: "Desk PC"))
        let again = PairingStore(backing: mem)
        XCTAssertEqual(again.paired?.label, "Desk PC")
        XCTAssertEqual(again.paired?.pcPub, pcPub)
        XCTAssertNil(again.pendingToken)
        XCTAssertEqual(again.existingStaticPublic(), store.existingStaticPublic())

        let notified = Counter()
        again.addObserver { notified.bump() }
        again.unpair()
        XCTAssertNil(again.paired)
        XCTAssertNil(again.existingStaticPublic())
        XCTAssertNil(PairingStore(backing: mem).paired)
        XCTAssertGreaterThanOrEqual(notified.value, 1)
    }

    func testExpiredPendingTokenIsDropped() throws {
        let clock = MutableClock()
        let store = PairingStore(backing: MemorySecretStore(), now: { clock.now })
        let token = PairingToken(token: tok, pcId: pcId, pcPub: pcPub, name: "Desk PC", expiresAt: clock.now.addingTimeInterval(300))
        try store.beginPairing(token)
        XCTAssertNotNil(store.pendingToken)
        clock.now = clock.now.addingTimeInterval(301)
        XCTAssertNil(store.pendingToken)
        XCTAssertFalse(store.isPairedOrPending)
        XCTAssertNil(store.makeResponder())
    }

    func testBonjourNameAndId() {
        let pub = Data(repeating: 0x42, count: 32)
        let id = BonjourPublisher.idHex(forStaticPublic: pub)
        XCTAssertEqual(id.count, 8)
        XCTAssertEqual(id, SHA256.hash(data: pub).prefix(4).map { String(format: "%02x", $0) }.joined())
        XCTAssertEqual(BonjourPublisher.serviceName(id: id), "RaaiCam-" + id)
    }
}

extension Result {
    var failureValue: Failure? {
        if case .failure(let e) = self { return e }
        return nil
    }
}

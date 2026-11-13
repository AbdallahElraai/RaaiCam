import XCTest
import CryptoKit
@testable import RaaiCam

final class FrameServerTwoSlotTests: XCTestCase {
    private func start(_ server: FrameServer) throws -> UInt16 {
        server.start()
        let deadline = Date().addingTimeInterval(5)
        while server.boundPort == 0 {
            if Date() > deadline { XCTFail("server did not start"); throw NSError(domain: "timeout", code: 1) }
            Thread.sleep(forTimeInterval: 0.01)
        }
        return server.boundPort
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 5, _ cond: () -> Bool) throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !cond() {
            if Date() > deadline { XCTFail("timed out waiting for \(what)"); throw NSError(domain: "timeout", code: 1) }
            Thread.sleep(forTimeInterval: 0.01)
        }
    }

    func testUnpairedNetworkPeerClosed() throws {
        let fx = try Fixture(paired: false)
        let server = FrameServer(port: 0, authority: fx.store)
        let refused = EventLog()
        server.onRejectedPeer = { refused.add($0) }
        let port = try start(server)

        // a network-like peer gets nothing at all (not even HELLO) and is closed
        server.testKindQueue = [.wifi]
        let net = try XCTUnwrap(TestClient(port: port))
        defer { net.close() }
        net.setTimeout(3)
        XCTAssertTrue(net.readFrames(count: 1).isEmpty)
        try waitUntil("refusal reported") { !refused.all.isEmpty }

        // a loopback peer gets the plaintext HELLO only, and is closed as soon as it tries AUTH
        let lo = try XCTUnwrap(TestClient(port: port))
        defer { lo.close() }
        let hello = lo.readFrames(count: 1)
        XCTAssertEqual(hello.first?.type, FrameType.hello.rawValue)
        lo.send(AuthedClient.helloPC)
        lo.send(FrameCodec.encode(.auth, Data(repeating: 1, count: 81)))
        XCTAssertTrue(lo.readFrames(count: 1).isEmpty)
    }

    func testUnauthenticatedNetworkPeerClosedAfterTimeout() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        server.authTimeout = 0.4
        let port = try start(server)
        server.testKindQueue = [.wifi]
        let net = try XCTUnwrap(TestClient(port: port))
        defer { net.close() }
        let started = Date()
        let hello = net.readFrames(count: 1)
        XCTAssertEqual(hello.first?.type, FrameType.hello.rawValue)
        // nothing else arrives; the server closes the idle unauthenticated connection
        XCTAssertTrue(net.readFrames(count: 1).isEmpty)
        XCTAssertLessThan(Date().timeIntervalSince(started), 2.5)
    }

    func testUnauthenticatedConnectionNeverDisplacesLiveSession() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let events = EventLog()
        server.onSessionStarted = { gen, kind in events.add("s\(gen):\(kind.rawValue)") }
        server.onSessionEnded = { gen, kind in events.add("e\(gen):\(kind.rawValue)") }
        let port = try start(server)
        let a = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { a.close() }
        try waitUntil("A started") { events.all == ["s1:usb"] }

        // an intruder on the same slot kind that never authenticates (garbage AUTH1) is closed
        let intruder = try XCTUnwrap(TestClient(port: port))
        defer { intruder.close() }
        XCTAssertEqual(intruder.readFrames(count: 1).first?.type, FrameType.hello.rawValue)
        intruder.send(AuthedClient.helloPC)
        intruder.send(FrameCodec.encode(.auth, Data(repeating: 9, count: 81)))
        XCTAssertTrue(intruder.readFrames(count: 1).isEmpty)

        // a plaintext non-handshake frame is also fatal
        let rude = try XCTUnwrap(TestClient(port: port))
        defer { rude.close() }
        _ = rude.readFrames(count: 1)
        rude.send(FrameCodec.encode(.cmd, Data(#"{"cmd":"stop"}"#.utf8)))
        XCTAssertTrue(rude.readFrames(count: 1).isEmpty)

        // the live session is untouched and still answers pings
        try a.sendInner(.ping, Data([0, 0, 0, 0, 0, 0, 0, 5]))
        XCTAssertEqual(a.readInner(count: 1).first?.type, FrameType.pong.rawValue)
        XCTAssertEqual(events.all, ["s1:usb"])
    }

    func testV1HelloRejected() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        let c = try XCTUnwrap(TestClient(port: port))
        defer { c.close() }
        _ = c.readFrames(count: 1)
        c.send(FrameCodec.encode(.hello, Data(#"{"proto":1,"role":"pc"}"#.utf8)))
        XCTAssertTrue(c.readFrames(count: 1).isEmpty)
    }

    func testPairingHandshakeThroughServer() throws {
        let fx = try Fixture(paired: false)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        let pcPub = fx.pcKey.publicKey.rawRepresentation
        let tokenBytes = Data(repeating: 0xB1, count: 16)
        let token = PairingToken(token: tokenBytes, pcId: fx.pcId, pcPub: pcPub, name: "Desk PC",
                                 expiresAt: Date().addingTimeInterval(300))
        try fx.store.beginPairing(token)

        // a PC holding the wrong token cannot pair, and does not burn the real token
        let rogue = TestInitiator(sI: fx.pcKey, pcId: fx.pcId, pairedPhonePub: nil, token: Data(repeating: 0, count: 16))
        XCTAssertThrowsError(try AuthedClient.connect(port: port, initiator: rogue))
        XCTAssertNotNil(fx.store.pendingToken)

        let ini = TestInitiator(sI: fx.pcKey, pcId: fx.pcId, pairedPhonePub: nil, token: tokenBytes)
        let client = try AuthedClient.connect(port: port, initiator: ini)
        defer { client.close() }
        XCTAssertEqual(fx.store.paired?.pcPub, pcPub)
        XCTAssertEqual(fx.store.paired?.label, "Desk PC")
        XCTAssertNil(fx.store.pendingToken)

        // single use: the same token is dead now
        let again = TestInitiator(sI: fx.pcKey, pcId: fx.pcId, pairedPhonePub: nil, token: tokenBytes)
        XCTAssertThrowsError(try AuthedClient.connect(port: port, initiator: again))
    }

    func testTwoSlotsVideoOnlyOnRouted() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let events = EventLog()
        server.onSessionStarted = { gen, kind in events.add("s\(gen):\(kind.rawValue)") }
        server.onSessionEnded = { gen, kind in events.add("e\(gen):\(kind.rawValue)") }
        let port = try start(server)

        server.testKindQueue = [.usb, .wifi]
        let usb = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { usb.close() }
        try waitUntil("usb started") { events.all.contains("s1:usb") }
        let wifi = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { wifi.close() }
        try waitUntil("wifi started") { events.all.contains("s2:wifi") }
        XCTAssertEqual(server.connectedKinds, [.usb, .wifi])

        // default route: the loopback (usb) session
        server.sendVideo(Data(repeating: 1, count: 100))
        XCTAssertEqual(usb.readInner(count: 1).first?.type, FrameType.video.rawValue)
        XCTAssertTrue(wifi.readInner(count: 1, timeout: 0.5).isEmpty)

        // telemetry and log go to both sessions
        server.send(.telemetry, Data("{}".utf8), to: .usb)
        server.send(.telemetry, Data("{}".utf8), to: .wifi)
        XCTAssertEqual(usb.readInner(count: 1).first?.type, FrameType.telemetry.rawValue)
        XCTAssertEqual(wifi.readInner(count: 1).first?.type, FrameType.telemetry.rawValue)

        // explicit re-route to wifi: video follows, usb gets none
        server.setRoute(gen: 2, video: true)
        server.setRoute(gen: 1, video: false)
        server.sendVideo(Data(repeating: 2, count: 100))
        XCTAssertEqual(wifi.readInner(count: 1).first?.type, FrameType.video.rawValue)
        XCTAssertTrue(usb.readInner(count: 1, timeout: 0.5).isEmpty)

        // usb disappears: wifi keeps the route
        usb.close()
        try waitUntil("usb ended") { events.all.contains("e1:usb") }
        server.sendVideo(Data(repeating: 3, count: 100))
        XCTAssertEqual(wifi.readInner(count: 1).first?.type, FrameType.video.rawValue)
        XCTAssertEqual(server.connectedKinds, [.wifi])
    }

    func testUnpairClosesAllSessions() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let events = EventLog()
        server.onSessionEnded = { gen, kind in events.add("e\(gen):\(kind.rawValue)") }
        let port = try start(server)
        server.testKindQueue = [.usb, .wifi]
        let a = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { a.close() }
        let b = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { b.close() }
        server.closeAll()
        try waitUntil("both ended") { events.all.count == 2 }
        XCTAssertTrue(server.connectedKinds.isEmpty)
    }
}

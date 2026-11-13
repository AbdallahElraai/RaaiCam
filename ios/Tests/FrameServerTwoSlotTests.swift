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

    // MARK: fix round 1

    func testPreAuthOversizeAndWrongTypeClosedAtHeader() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        // HELLO announcing 4096 bytes (> 1024 pre-auth cap): closed from the header alone, payload never sent
        let a = try XCTUnwrap(TestClient(port: port))
        defer { a.close() }
        XCTAssertEqual(a.readFrames(count: 1).first?.type, FrameType.hello.rawValue)
        a.send(Data([0x01, 0x00, 0x00, 0x10, 0x00]))
        XCTAssertTrue(a.readFrames(count: 1).isEmpty)
        // a multi-MiB SEALED header before AUTH: wrong type for the phase, closed at the header
        let b = try XCTUnwrap(TestClient(port: port))
        defer { b.close() }
        _ = b.readFrames(count: 1)
        b.send(Data([0x09, 0x00, 0x30, 0x00, 0x00]))
        XCTAssertTrue(b.readFrames(count: 1).isEmpty)
        // AUTH before HELLO
        let c = try XCTUnwrap(TestClient(port: port))
        defer { c.close() }
        _ = c.readFrames(count: 1)
        c.send(FrameCodec.encode(.auth, Data(repeating: 1, count: 81)))
        XCTAssertTrue(c.readFrames(count: 1).isEmpty)
    }

    func testWifiFloodCannotStarveUsb() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        server.authTimeout = 30
        let port = try start(server)
        server.testKindQueue = [.wifi, .wifi, .wifi, .wifi, .wifi]
        server.testPeerIPQueue = ["10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5"]
        var held = [TestClient]()
        defer { held.forEach { $0.close() } }
        for _ in 0..<FrameServer.maxUnauthenticatedWifi {
            let c = try XCTUnwrap(TestClient(port: port))
            held.append(c)
            XCTAssertEqual(c.readFrames(count: 1).first?.type, FrameType.hello.rawValue)
        }
        // the wifi cap is full: the next network peer is closed without a HELLO
        let over = try XCTUnwrap(TestClient(port: port))
        defer { over.close() }
        XCTAssertTrue(over.readFrames(count: 1).isEmpty)
        // a USB (loopback) connection still gets served and authenticates
        let usb = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { usb.close() }
        XCTAssertEqual(server.connectedKinds, [.usb])
    }

    func testPerSourceIPCap() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        server.authTimeout = 30
        let port = try start(server)
        server.testKindQueue = [.wifi, .wifi, .wifi, .wifi]
        server.testPeerIPQueue = ["10.0.0.9", "10.0.0.9", "10.0.0.9", "10.0.0.8"]
        var held = [TestClient]()
        defer { held.forEach { $0.close() } }
        for _ in 0..<FrameServer.maxUnauthenticatedWifiPerIP {
            let c = try XCTUnwrap(TestClient(port: port))
            held.append(c)
            XCTAssertEqual(c.readFrames(count: 1).first?.type, FrameType.hello.rawValue)
        }
        let third = try XCTUnwrap(TestClient(port: port))
        defer { third.close() }
        XCTAssertTrue(third.readFrames(count: 1).isEmpty)       // same IP: over its cap
        let other = try XCTUnwrap(TestClient(port: port))
        held.append(other)
        XCTAssertEqual(other.readFrames(count: 1).first?.type, FrameType.hello.rawValue) // other IP is fine
    }

    func testSameKindReplacementOnlyAfterAuth() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        let a = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { a.close() }
        let b = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { b.close() }
        // A was replaced (its socket is closed); B is the live usb session
        a.raw.setTimeout(3)
        XCTAssertTrue(a.raw.readFrames(count: 1).isEmpty)
        try b.sendInner(.ping, Data([0, 0, 0, 0, 0, 0, 0, 1]))
        XCTAssertEqual(b.readInner(count: 1).first?.type, FrameType.pong.rawValue)
        XCTAssertEqual(server.connectedKinds, [.usb])
    }

    func testUnknownStaticKeyAndWrongPcIdRejected() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        // mode 1 with an unknown S_I
        let stranger = TestInitiator(sI: Curve25519.KeyAgreement.PrivateKey(), pcId: fx.pcId,
                                     pairedPhonePub: fx.phonePub, token: nil)
        XCTAssertThrowsError(try AuthedClient.connect(port: port, initiator: stranger))
        // mode 1 with the right key but a different pcId
        let wrongId = TestInitiator(sI: fx.pcKey, pcId: Data(repeating: 9, count: 16), pairedPhonePub: fx.phonePub, token: nil)
        XCTAssertThrowsError(try AuthedClient.connect(port: port, initiator: wrongId))
        XCTAssertTrue(server.connectedKinds.isEmpty)
        // the genuine PC still gets in
        let ok = try AuthedClient.connect(port: port, initiator: fx.initiator())
        ok.close()
    }

    func testPairingWithWrongPcIdRefusedAndTokenKept() throws {
        let fx = try Fixture(paired: false)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        let tokenBytes = Data(repeating: 0xB1, count: 16)
        try fx.store.beginPairing(PairingToken(token: tokenBytes, pcId: fx.pcId, pcPub: fx.pcKey.publicKey.rawRepresentation,
                                               name: "Desk PC", expiresAt: Date().addingTimeInterval(300)))
        let bad = TestInitiator(sI: fx.pcKey, pcId: Data(repeating: 9, count: 16), pairedPhonePub: nil, token: tokenBytes)
        XCTAssertThrowsError(try AuthedClient.connect(port: port, initiator: bad))
        XCTAssertNotNil(fx.store.pendingToken)
        XCTAssertNil(fx.store.paired)
    }

    func testSealedCounterReplayClosesSession() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let events = EventLog()
        server.onSessionEnded = { gen, _ in events.add("e\(gen)") }
        let port = try start(server)
        let a = try AuthedClient.connect(port: port, initiator: fx.initiator())
        defer { a.close() }
        let payload = try a.channel.seal(innerType: FrameType.ping.rawValue, payload: Data([0, 0, 0, 0, 0, 0, 0, 7]))
        let frame = FrameCodec.encode(.sealed, payload)
        a.raw.send(frame)
        XCTAssertEqual(a.readInner(count: 1).first?.type, FrameType.pong.rawValue)
        a.raw.send(frame) // replayed counter
        XCTAssertTrue(a.raw.readFrames(count: 1).isEmpty)
        try waitUntil("session ended") { events.all == ["e1"] }
    }

    func testPhoneToPcFrameTypesFromPcCloseSession() throws {
        let fx = try Fixture(paired: true)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        for t in [FrameType.video, .telemetry, .log] {
            let a = try AuthedClient.connect(port: port, initiator: fx.initiator())
            defer { a.close() }
            try a.sendInner(t, Data([1, 2, 3]))
            a.raw.setTimeout(3)
            XCTAssertTrue(a.raw.readFrames(count: 1).isEmpty, "type \(t)")
        }
    }

    func testConcurrentPairingHandshakesOneToken() throws {
        let fx = try Fixture(paired: false)
        let server = FrameServer(port: 0, authority: fx.store)
        let port = try start(server)
        let tokenBytes = Data(repeating: 0xB1, count: 16)
        try fx.store.beginPairing(PairingToken(token: tokenBytes, pcId: fx.pcId, pcPub: fx.pcKey.publicKey.rawRepresentation,
                                               name: "Desk PC", expiresAt: Date().addingTimeInterval(300)))
        let i1 = TestInitiator(sI: fx.pcKey, pcId: fx.pcId, pairedPhonePub: nil, token: tokenBytes)
        let i2 = TestInitiator(sI: fx.pcKey, pcId: fx.pcId, pairedPhonePub: nil, token: tokenBytes)

        let r1 = try XCTUnwrap(TestClient(port: port))
        defer { r1.close() }
        r1.send(AuthedClient.helloPC)
        _ = r1.readFrames(count: 1)
        r1.send(FrameCodec.encode(.auth, i1.createAuth1()))
        let auth2 = r1.readFrames(count: 1)
        XCTAssertEqual(auth2.first?.type, FrameType.auth.rawValue) // first claim is in flight

        let r2 = try XCTUnwrap(TestClient(port: port))
        defer { r2.close() }
        r2.send(AuthedClient.helloPC)
        _ = r2.readFrames(count: 1)
        r2.send(FrameCodec.encode(.auth, i2.createAuth1()))
        XCTAssertTrue(r2.readFrames(count: 1).isEmpty)               // second claim refused

        // the first handshake can still finish
        _ = try i1.readAuth2(try XCTUnwrap(auth2.first).payload)
        r1.send(FrameCodec.encode(.auth, try i1.createAuth3()))
        let ch = SealedChannel(keys: try XCTUnwrap(i1.keys), role: .initiator)
        let first = r1.readFrames(count: 1)
        XCTAssertEqual(try ch.open(try XCTUnwrap(first.first).payload).type, FrameType.info.rawValue)
        XCTAssertNotNil(fx.store.paired)
    }
}

import XCTest
import Darwin
@testable import RaaiCam

/// Minimal blocking TCP client for talking to a FrameServer over loopback.
final class TestClient {
    let fd: Int32
    private var decoder = FrameDecoder()

    init?(port: UInt16) {
        fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr = in_addr(s_addr: UInt32(0x7F000001).bigEndian)
        let r = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard r == 0 else { Darwin.close(fd); return nil }
        var tv = timeval(tv_sec: 3, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    }

    func send(_ data: Data) {
        data.withUnsafeBytes { raw in _ = Darwin.write(fd, raw.baseAddress, raw.count) }
    }

    /// Reads until `count` frames have arrived (or the 3 s receive timeout / EOF hits).
    func readFrames(count: Int) -> [Frame] {
        var got = [Frame]()
        var buf = [UInt8](repeating: 0, count: 4096)
        while got.count < count {
            let n = read(fd, &buf, buf.count)
            if n <= 0 { break }
            got += (try? decoder.feed(Data(buf[0..<n]))) ?? []
        }
        return got
    }

    func close() { Darwin.close(fd) }
}

final class EventLog {
    private let lock = NSLock()
    private var items = [String]()
    func add(_ s: String) { lock.lock(); items.append(s); lock.unlock() }
    var all: [String] { lock.lock(); defer { lock.unlock() }; return items }
}

final class FrameServerTests: XCTestCase {
    private var server: FrameServer!

    override func setUpWithError() throws {
        server = FrameServer(port: 0)
    }

    private func startServer() throws -> UInt16 {
        server.start()
        try waitUntil("server listening") { self.server.boundPort != 0 }
        return server.boundPort
    }

    private func waitUntil(_ what: String, timeout: TimeInterval = 5, _ cond: () -> Bool) throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !cond() {
            if Date() > deadline { XCTFail("timed out waiting for \(what)"); throw NSError(domain: "timeout", code: 1) }
            Thread.sleep(forTimeInterval: 0.01)
        }
    }

    func testClientReceivesHello() throws {
        let srv: FrameServer = server
        server.onClientConnected = { _ in
            srv.send(.hello, Hello.phone(version: "1.0.0", device: "test", os: "iOS test"))
        }
        let port = try startServer()
        let client = try XCTUnwrap(TestClient(port: port))
        defer { client.close() }
        let frames = client.readFrames(count: 1)
        XCTAssertEqual(frames.count, 1)
        XCTAssertEqual(frames.first?.type, FrameType.hello.rawValue)
        let hello = try Hello.parse(try XCTUnwrap(frames.first).payload)
        XCTAssertEqual(hello.proto, 1)
        XCTAssertEqual(hello.role, "phone")
    }

    func testPartialWritesReassembleAndPingGetsPong() throws {
        let received = EventLog()
        server.onFrame = { frame in received.add("type\(frame.type)") }
        let port = try startServer()
        let client = try XCTUnwrap(TestClient(port: port))
        defer { client.close() }

        // PING (answered by the server itself) dribbled in one byte at a time
        let ping = FrameCodec.encode(.ping, Data([0, 0, 0, 0, 0, 0x0f, 0x42, 0x40]))
        for b in ping { client.send(Data([b])); Thread.sleep(forTimeInterval: 0.002) }
        let pong = client.readFrames(count: 1)
        XCTAssertEqual(pong.first?.type, FrameType.pong.rawValue)
        XCTAssertEqual(pong.first?.payload.count, 16)
        XCTAssertEqual(pong.first?.payload.readBE64(at: 0), 1_000_000)

        // CMD (forwarded to onFrame) also dribbled
        let cmd = FrameCodec.encode(.cmd, Data(#"{"cmd":"stop"}"#.utf8))
        for b in cmd { client.send(Data([b])); Thread.sleep(forTimeInterval: 0.002) }
        try waitUntil("cmd frame") { received.all.count == 1 }
        XCTAssertEqual(received.all, ["type3"])
    }

    func testClientReplacementDoesNotSkewPendingCount() throws {
        let events = EventLog()
        server.onClientConnected = { gen in events.add("c\(gen)") }
        server.onClientDisconnected = { gen in events.add("d\(gen)") }
        let port = try startServer()
        let a = try XCTUnwrap(TestClient(port: port))
        defer { a.close() }
        try waitUntil("A connected") { events.all.contains("c1") }

        // queue video on A and replace it immediately: stale completions must not decrement B's counter
        for _ in 0..<3 { server.sendVideo(Data(repeating: 7, count: 200_000)) }
        let b = try XCTUnwrap(TestClient(port: port))
        defer { b.close() }
        try waitUntil("B connected") { events.all.contains("c2") }
        Thread.sleep(forTimeInterval: 0.3)
        XCTAssertEqual(server.pendingVideoCount, 0)

        for _ in 0..<2 { server.sendVideo(Data(repeating: 1, count: 100)) }
        let frames = b.readFrames(count: 2)
        XCTAssertEqual(frames.count, 2)
        try waitUntil("drain") { self.server.pendingVideoCount == 0 }
        XCTAssertGreaterThanOrEqual(server.pendingVideoCount, 0)
    }

    func testStaleDisconnectOrdering() throws {
        let events = EventLog()
        server.onClientConnected = { gen in events.add("c\(gen)") }
        server.onClientDisconnected = { gen in events.add("d\(gen)") }
        let port = try startServer()
        let a = try XCTUnwrap(TestClient(port: port))
        try waitUntil("A connected") { events.all == ["c1"] }
        let b = try XCTUnwrap(TestClient(port: port))
        defer { b.close() }
        // replacement reports exactly: old disconnected, then new connected
        try waitUntil("B connected") { events.all == ["c1", "d1", "c2"] }
        a.close()
        Thread.sleep(forTimeInterval: 0.4)
        // A's reader exiting late must not emit a second disconnect for gen 1 (or touch gen 2)
        XCTAssertEqual(events.all, ["c1", "d1", "c2"])
        b.close()
        try waitUntil("B disconnected") { events.all == ["c1", "d1", "c2", "d2"] }
    }
}

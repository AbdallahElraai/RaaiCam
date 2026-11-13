import Foundation
import Darwin

/// BSD-socket TCP server (NWListener does not accept connections arriving via
/// usbmux). Bound to INADDR_ANY exactly like the proven Phase 0 spike, but any
/// peer that is not loopback (or the device itself) is closed right after accept
/// (USB-only in 1B). One client at a time; a new loopback client replaces the old one.
///
/// Threading: one listener thread (bind with backoff, poll+accept), one reader thread per
/// client, all writes serialized on `writeQueue`. Only `writeQueue` closes a client fd, after the
/// client has been retired, so a write can never hit a recycled fd. Every connect/disconnect event
/// carries a client generation so consumers can ignore stale events.
final class FrameServer {
    static let maxPendingVideo = 3

    var onListening: (() -> Void)?
    var onError: ((String) -> Void)?
    var onClientConnected: ((UInt64) -> Void)?
    var onClientDisconnected: ((UInt64) -> Void)?
    var onRejectedPeer: ((String) -> Void)?
    var onFrame: ((Frame) -> Void)?
    var onLog: ((String) -> Void)?
    /// PING frames are answered on the reader thread (never queued behind the session queue).
    var answerPings = true

    private let port: UInt16
    private let writeQueue = DispatchQueue(label: "raaicam.server.write")
    private let lock = NSLock()
    private let wake = DispatchSemaphore(value: 0)
    private var started = false
    private var rebindRequested = false
    private var backoff = 0.5
    private var currentFD: Int32 = -1
    private var generation: UInt64 = 0
    private var pendingVideo = 0
    private var droppedSinceLog = 0
    private var lastDropLog = Date.distantPast
    private var boundPortValue: UInt16 = 0

    init(port: UInt16 = 47820) { self.port = port }

    private static func errnoText() -> String { String(cString: strerror(errno)) }

    /// Actual bound port (differs from the requested one only when port 0 was requested).
    var boundPort: UInt16 { lock.lock(); defer { lock.unlock() }; return boundPortValue }
    /// Pending VIDEO writes for the current client (exposed for tests).
    var pendingVideoCount: Int { lock.lock(); defer { lock.unlock() }; return pendingVideo }

    func start() {
        lock.lock()
        if started { lock.unlock(); return }
        started = true
        lock.unlock()
        let thread = Thread { [weak self] in self?.listenerLoop() }
        thread.name = "raaicam.listener"
        thread.start()
    }

    /// Call when the app returns to the foreground: with no client attached, re-open the listener
    /// (and cut short any backoff wait).
    func restartIfNeeded() {
        lock.lock()
        if currentFD < 0 { rebindRequested = true }
        lock.unlock()
        wake.signal()
    }

    // MARK: listener

    private struct ListenError: Error { let message: String }
    private enum AcceptEnd { case rebind, failed(String) }

    private func listenerLoop() {
        while true {
            lock.lock(); rebindRequested = false; lock.unlock()
            switch openListener() {
            case .failure(let e):
                onError?(e.message)
                waitBackoff()
            case .success(let fd):
                onListening?()
                let end = acceptLoop(fd)
                close(fd)
                if case .failed(let msg) = end {
                    onError?(msg)
                    waitBackoff()
                } else {
                    backoff = 0.5
                }
            }
        }
    }

    private func waitBackoff() {
        let delay = backoff
        backoff = min(backoff * 2, 5)
        _ = wake.wait(timeout: .now() + delay)
    }

    private func openListener() -> Result<Int32, ListenError> {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return .failure(ListenError(message: "socket: \(Self.errnoText())")) }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr = in_addr(s_addr: INADDR_ANY.bigEndian)
        let bindResult = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else {
            let msg = Self.errnoText(); close(fd)
            return .failure(ListenError(message: "bind: \(msg)"))
        }
        guard listen(fd, 8) == 0 else {
            let msg = Self.errnoText(); close(fd)
            return .failure(ListenError(message: "listen: \(msg)"))
        }
        var actual = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        let r = withUnsafeMutablePointer(to: &actual) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) }
        }
        lock.lock()
        boundPortValue = r == 0 ? UInt16(bigEndian: actual.sin_port) : port
        lock.unlock()
        return .success(fd)
    }

    private func acceptLoop(_ listenFD: Int32) -> AcceptEnd {
        while true {
            var pfd = pollfd(fd: listenFD, events: Int16(POLLIN), revents: 0)
            let pr = poll(&pfd, 1, 500)
            if pr < 0 {
                if errno == EINTR { continue }
                return .failed("poll: \(Self.errnoText())")
            }
            lock.lock(); let rebind = rebindRequested; lock.unlock()
            if rebind { return .rebind }
            if pr == 0 { continue }

            let client = accept(listenFD, nil, nil)
            if client < 0 {
                if errno == EINTR || errno == ECONNABORTED || errno == EAGAIN { continue }
                return .failed("accept: \(Self.errnoText())")
            }
            let peer = Self.classifyPeer(client)
            guard peer.allowed else {
                NSLog("RaaiCam rejected non-loopback peer %@", peer.description)
                onRejectedPeer?(peer.description)
                close(client)
                continue
            }
            backoff = 0.5
            var one: Int32 = 1
            setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
            setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
            var tv = timeval(tv_sec: 2, tv_usec: 0)
            setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

            lock.lock()
            let old = currentFD
            generation += 1
            let gen = generation
            currentFD = client
            pendingVideo = 0
            if old >= 0 { shutdown(old, SHUT_RDWR) } // old reader sees EOF; it will not report a disconnect (replaced)
            let oldGen = gen - 1
            lock.unlock()
            if old >= 0 { onClientDisconnected?(oldGen) }
            onClientConnected?(gen)
            let thread = Thread { [weak self] in self?.readLoop(client, gen: gen) }
            thread.name = "raaicam.reader"
            thread.start()
        }
    }

    // MARK: sending

    /// Control-type frames are never dropped.
    func send(_ type: FrameType, _ payload: Data) {
        lock.lock(); let fd = currentFD; let gen = generation; lock.unlock()
        sendOn(fd: fd, gen: gen, type: type, payload: payload)
    }

    private func sendOn(fd: Int32, gen: UInt64, type: FrameType, payload: Data) {
        guard fd >= 0 else { return }
        let bytes = FrameCodec.encode(type, payload)
        writeQueue.async { [weak self] in self?.write(bytes, fd: fd, gen: gen) }
    }

    /// Returns false if the frame was dropped for back-pressure (more than 3 pending).
    @discardableResult
    func sendVideo(_ payload: Data) -> Bool {
        lock.lock()
        let fd = currentFD, gen = generation
        if fd < 0 { lock.unlock(); return false }
        if pendingVideo > Self.maxPendingVideo {
            droppedSinceLog += 1
            var message: String?
            if Date().timeIntervalSince(lastDropLog) >= 5 {
                message = "W dropped \(droppedSinceLog) video frame(s): send queue backed up"
                droppedSinceLog = 0
                lastDropLog = Date()
            }
            lock.unlock()
            if let m = message { onLog?(m) }
            return false
        }
        pendingVideo += 1
        lock.unlock()
        let bytes = FrameCodec.encode(.video, payload)
        writeQueue.async { [weak self] in
            guard let self = self else { return }
            self.write(bytes, fd: fd, gen: gen)
            // Only the generation that incremented the counter may decrement it (replacement resets it).
            self.lock.lock()
            if self.generation == gen && self.pendingVideo > 0 { self.pendingVideo -= 1 }
            self.lock.unlock()
        }
        return true
    }

    /// Drops the current client (e.g. bad HELLO).
    func disconnectClient() {
        lock.lock()
        if currentFD >= 0 { shutdown(currentFD, SHUT_RDWR) }
        lock.unlock()
    }

    // runs on writeQueue
    private func write(_ data: Data, fd: Int32, gen: UInt64) {
        lock.lock(); let live = (generation == gen && currentFD == fd); lock.unlock()
        guard live else { return }
        let ok: Bool = data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Bool in
            guard let base = raw.baseAddress else { return true }
            var sent = 0
            while sent < raw.count {
                let n = Darwin.write(fd, base + sent, raw.count - sent)
                if n < 0 {
                    if errno == EINTR { continue }
                    return false // includes EAGAIN from the 2 s SO_SNDTIMEO
                }
                sent += n
            }
            return true
        }
        if !ok {
            lock.lock()
            if generation == gen && currentFD == fd { shutdown(fd, SHUT_RDWR) }
            lock.unlock()
        }
    }

    // MARK: reading

    private func readLoop(_ fd: Int32, gen: UInt64) {
        var decoder = FrameDecoder()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        readLoop: while true {
            let n = read(fd, &buffer, buffer.count)
            if n < 0 {
                if errno == EINTR { continue }
                break
            }
            if n == 0 { break }
            do {
                for frame in try decoder.feed(Data(buffer[0..<n])) {
                    if answerPings && frame.type == FrameType.ping.rawValue {
                        let recv = PhoneClock.nowUs()
                        if let sent = Payloads.parsePing(frame.payload) {
                            sendOn(fd: fd, gen: gen, type: .pong, payload: Payloads.pong(pcSendUs: sent, phoneRecvUs: recv))
                        }
                        continue
                    }
                    onFrame?(frame)
                }
            } catch {
                onLog?("E protocol error: \(error)")
                break readLoop
            }
        }
        lock.lock()
        let wasCurrent = (generation == gen && currentFD == fd)
        if wasCurrent { currentFD = -1 }
        lock.unlock()
        // Close on the write queue: earlier queued writes finish first and later ones see a retired generation.
        writeQueue.async { close(fd) }
        if wasCurrent { onClientDisconnected?(gen) }
    }

    // MARK: peer check

    private static func address(_ fd: Int32, peer: Bool) -> (family: Int32, bytes: [UInt8])? {
        var ss = sockaddr_storage()
        var len = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let r = withUnsafeMutablePointer(to: &ss) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                peer ? getpeername(fd, $0, &len) : getsockname(fd, $0, &len)
            }
        }
        guard r == 0 else { return nil }
        switch Int32(ss.ss_family) {
        case AF_INET:
            let bytes: [UInt8] = withUnsafePointer(to: &ss) {
                $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { p in
                    withUnsafeBytes(of: p.pointee.sin_addr) { Array($0) }
                }
            }
            return (AF_INET, bytes)
        case AF_INET6:
            let bytes: [UInt8] = withUnsafePointer(to: &ss) {
                $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { p in
                    withUnsafeBytes(of: p.pointee.sin6_addr) { Array($0) }
                }
            }
            return (AF_INET6, bytes)
        default:
            return nil
        }
    }

    static func classifyPeer(_ fd: Int32) -> (allowed: Bool, description: String) {
        guard let peer = address(fd, peer: true) else { return (false, "unknown") }
        let local = address(fd, peer: false)?.bytes ?? []
        return (PeerAddress.isAllowed(family: peer.family, peer: peer.bytes, local: local),
                PeerAddress.describe(family: peer.family, bytes: peer.bytes))
    }
}

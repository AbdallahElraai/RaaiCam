import Foundation
import Darwin

/// BSD-socket TCP server (NWListener does not accept connections arriving via
/// usbmux). Bound to INADDR_ANY exactly like the proven Phase 0 spike, but any
/// peer that is not loopback is closed right after accept (USB-only in 1B).
/// One client at a time; a new loopback client replaces the old one.
///
/// Threading: accept loop on its own thread, one reader thread per client,
/// all writes serialized on `writeQueue`. Only `writeQueue` closes an fd, after
/// the client has been retired, so a write can never hit a recycled fd.
final class FrameServer {
    static let maxPendingVideo = 3

    var onListening: (() -> Void)?
    var onError: ((String) -> Void)?
    var onClientConnected: (() -> Void)?
    var onClientDisconnected: (() -> Void)?
    var onFrame: ((Frame) -> Void)?
    var onLog: ((String) -> Void)?

    private let port: UInt16
    private let writeQueue = DispatchQueue(label: "raaicam.server.write")
    private let lock = NSLock()
    private var currentFD: Int32 = -1
    private var generation: UInt64 = 0
    private var pendingVideo = 0
    private var droppedSinceLog = 0
    private var lastDropLog = Date.distantPast

    init(port: UInt16 = 47820) { self.port = port }

    private static func errnoText() -> String { String(cString: strerror(errno)) }

    func start() {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { onError?("socket: \(Self.errnoText())"); return }
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
            let msg = Self.errnoText(); close(fd); onError?("bind: \(msg)"); return
        }
        guard listen(fd, 8) == 0 else {
            let msg = Self.errnoText(); close(fd); onError?("listen: \(msg)"); return
        }
        onListening?()
        let thread = Thread { [weak self] in self?.acceptLoop(fd) }
        thread.name = "raaicam.accept"
        thread.start()
    }

    // MARK: sending

    /// Control-type frames are never dropped.
    func send(_ type: FrameType, _ payload: Data) {
        let bytes = FrameCodec.encode(type, payload)
        lock.lock(); let fd = currentFD; let gen = generation; lock.unlock()
        guard fd >= 0 else { return }
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
            self.lock.lock(); self.pendingVideo -= 1; self.lock.unlock()
        }
        return true
    }

    /// Drops the current client (e.g. bad HELLO).
    func disconnectClient() {
        lock.lock(); let fd = currentFD; lock.unlock()
        if fd >= 0 { shutdown(fd, SHUT_RDWR) }
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
                    return false
                }
                sent += n
            }
            return true
        }
        if !ok { shutdown(fd, SHUT_RDWR) } // reader thread notices and retires the client
    }

    // MARK: accepting

    private func acceptLoop(_ listenFD: Int32) {
        while true {
            let client = accept(listenFD, nil, nil)
            if client < 0 {
                if errno == EINTR || errno == ECONNABORTED { continue }
                onError?("accept: \(Self.errnoText())")
                return
            }
            guard Self.peerIsLoopback(client) else {
                onLog?("W rejected non-loopback peer")
                close(client)
                continue
            }
            var one: Int32 = 1
            setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
            setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))

            lock.lock()
            let old = currentFD
            generation += 1
            let gen = generation
            currentFD = client
            pendingVideo = 0
            lock.unlock()
            if old >= 0 {
                shutdown(old, SHUT_RDWR) // old reader sees EOF; it will not report a disconnect (replaced)
                onClientDisconnected?()
            }
            onClientConnected?()
            let thread = Thread { [weak self] in self?.readLoop(client, gen: gen) }
            thread.name = "raaicam.reader"
            thread.start()
        }
    }

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
                for frame in try decoder.feed(Data(buffer[0..<n])) { onFrame?(frame) }
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
        if wasCurrent { onClientDisconnected?() }
    }

    // MARK: peer check

    static func peerIsLoopback(_ fd: Int32) -> Bool {
        var ss = sockaddr_storage()
        var len = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let r = withUnsafeMutablePointer(to: &ss) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getpeername(fd, $0, &len) }
        }
        guard r == 0 else { return false }
        switch Int32(ss.ss_family) {
        case AF_INET:
            return withUnsafePointer(to: &ss) {
                $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                    PeerAddress.isLoopback(v4: UInt32(bigEndian: $0.pointee.sin_addr.s_addr))
                }
            }
        case AF_INET6:
            return withUnsafePointer(to: &ss) {
                $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { p in
                    let bytes = withUnsafeBytes(of: p.pointee.sin6_addr) { Array($0) }
                    return PeerAddress.isLoopback(v6: bytes)
                }
            }
        default:
            return false
        }
    }
}

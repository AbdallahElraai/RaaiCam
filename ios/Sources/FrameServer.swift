import Foundation
import Darwin

/// Which physical path a session arrived on.
enum LinkKind: String, CaseIterable {
    case usb   // loopback (usbmux) or the device connecting to itself
    case wifi  // any other peer
}

/// BSD-socket TCP server (NWListener does not accept connections arriving via usbmux), bound to
/// INADDR_ANY like the proven Phase 0 spike.
///
/// Protocol v2: every connection runs HELLO (plaintext, proto 2) -> AUTH (3 messages, 5 s total) ->
/// SEALED. Right after AUTH3 the server sends a sealed INFO frame, then the session behaves like 1B.
/// Two slots exist, one per `LinkKind`. A newly authenticated session replaces the live session of the
/// same kind only after it authenticated; unauthenticated connections never displace anything.
///
/// Threading: one listener thread (bind with backoff, poll+accept), one reader thread per connection,
/// all writes (including sealing, so counters follow wire order) serialized on `writeQueue`. Only
/// `writeQueue` closes a connection fd, after the connection has been retired, so a write can never hit
/// a recycled fd. Every started/ended event carries a connection generation so consumers can ignore
/// stale events.
final class FrameServer {
    static let maxPendingVideo = 3
    static let maxUnauthenticated = 8

    var onListening: (() -> Void)?
    var onError: ((String) -> Void)?
    /// A session finished AUTH (generation, kind). Fired after INFO was queued.
    var onSessionStarted: ((UInt64, LinkKind) -> Void)?
    var onSessionEnded: ((UInt64, LinkKind) -> Void)?
    /// A network peer was refused because nothing is paired and no token is pending.
    var onRejectedPeer: ((String) -> Void)?
    /// Inner frames of authenticated sessions (PING is answered by the server and not forwarded).
    var onFrame: ((UInt64, LinkKind, Frame) -> Void)?
    var onLog: ((String) -> Void)?
    /// Slots or explicit routes changed (the routed session may be a different one now).
    var onRoutingChanged: (() -> Void)?
    /// PING frames are answered on the reader thread (never queued behind the session queue).
    var answerPings = true
    /// Total time allowed from accept to the end of AUTH.
    var authTimeout: TimeInterval = 5
    /// Payload of the sealed INFO frame sent first on every session.
    var infoPayload = Hello.info(version: "1.0.0", device: "iPhone", os: "iOS")
    /// Test seam: kinds to assign to the next accepted connections (consumed in order) instead of
    /// classifying the peer address. Lets a loopback test connection play a network peer.
    var testKindQueue = [LinkKind]()

    private let port: UInt16
    private let authority: ServerAuthority
    private let writeQueue = DispatchQueue(label: "raaicam.server.write")
    private let timeoutQueue = DispatchQueue(label: "raaicam.server.timeout")
    private let lock = NSLock()
    private let wake = DispatchSemaphore(value: 0)
    private var started = false
    private var rebindRequested = false
    private var backoff = 0.5
    private var generation: UInt64 = 0
    private var slots = [LinkKind: Conn]()
    private var unauthenticated = 0
    private var droppedSinceLog = 0
    private var lastDropLog = Date.distantPast
    private var boundPortValue: UInt16 = 0
    private var all = [UInt64: Conn]()

    /// One accepted connection. Mutable fields are guarded by `FrameServer.lock`.
    private final class Conn {
        let fd: Int32
        let id: UInt64
        let kind: LinkKind
        var retired = false
        var authenticated = false
        var channel: SealedChannel?
        var pendingVideo = 0
        var routeOverride: Bool?
        init(fd: Int32, id: UInt64, kind: LinkKind) { self.fd = fd; self.id = id; self.kind = kind }
    }

    init(port: UInt16 = 47820, authority: ServerAuthority) {
        self.port = port
        self.authority = authority
    }

    private static func errnoText() -> String { String(cString: strerror(errno)) }

    /// Actual bound port (differs from the requested one only when port 0 was requested).
    var boundPort: UInt16 { lock.lock(); defer { lock.unlock() }; return boundPortValue }
    /// Pending VIDEO writes across live sessions (exposed for tests).
    var pendingVideoCount: Int { lock.lock(); defer { lock.unlock() }; return slots.values.reduce(0) { $0 + $1.pendingVideo } }
    /// Kinds with a live authenticated session.
    var connectedKinds: Set<LinkKind> { lock.lock(); defer { lock.unlock() }; return Set(slots.keys) }

    func start() {
        lock.lock()
        if started { lock.unlock(); return }
        started = true
        lock.unlock()
        let thread = Thread { [weak self] in self?.listenerLoop() }
        thread.name = "raaicam.listener"
        thread.start()
    }

    /// Call when the app returns to the foreground: with no session attached, re-open the listener
    /// (and cut short any backoff wait).
    func restartIfNeeded() {
        lock.lock()
        if slots.isEmpty { rebindRequested = true }
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
            backoff = 0.5
            admit(client)
        }
    }

    /// Classifies the peer, applies the unpaired policy and starts the connection's reader thread.
    private func admit(_ client: Int32) {
        let peer = Self.classifyPeer(client)
        lock.lock()
        let kind: LinkKind = testKindQueue.isEmpty ? (peer.allowed ? .usb : .wifi) : testKindQueue.removeFirst()
        lock.unlock()

        // Nothing paired and no token pending: network peers get nothing at all.
        if kind == .wifi && !authority.isPairedOrPending {
            NSLog("RaaiCam refused network peer while unpaired")
            onRejectedPeer?(peer.description)
            close(client)
            return
        }

        var one: Int32 = 1
        setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
        var tv = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        lock.lock()
        if unauthenticated >= Self.maxUnauthenticated { lock.unlock(); close(client); return }
        unauthenticated += 1
        generation += 1
        let conn = Conn(fd: client, id: generation, kind: kind)
        all[conn.id] = conn
        lock.unlock()

        sendPlain(conn, .hello, Hello.phone())
        scheduleAuthTimeout(conn)
        let thread = Thread { [weak self] in self?.readLoop(conn) }
        thread.name = "raaicam.reader"
        thread.start()
    }

    private func scheduleAuthTimeout(_ c: Conn) {
        timeoutQueue.asyncAfter(deadline: .now() + authTimeout) { [weak self] in
            guard let self = self else { return }
            self.lock.lock()
            // not yet retired => the fd is still open (close is queued only after retirement)
            if !c.authenticated && !c.retired { shutdown(c.fd, SHUT_RDWR) }
            self.lock.unlock()
        }
    }

    // MARK: sending

    /// Control-type frame to one session's link. Never dropped.
    func send(_ type: FrameType, _ payload: Data, to kind: LinkKind) {
        lock.lock(); let c = slots[kind]; lock.unlock()
        if let c = c { enqueue(c, type.rawValue, payload) }
    }

    /// Control-type frame to every authenticated session.
    func sendAll(_ type: FrameType, _ payload: Data) {
        lock.lock(); let all = Array(slots.values); lock.unlock()
        for c in all { enqueue(c, type.rawValue, payload) }
    }

    private func enqueue(_ c: Conn, _ type: UInt8, _ payload: Data) {
        writeQueue.async { [weak self] in self?.writeSealed(c, type: type, payload: payload) }
    }

    /// The sessions that should receive VIDEO: an explicit `route` wins; otherwise the loopback session
    /// if present, else the network one. Caller holds `lock`.
    private func routedLocked() -> [Conn] {
        let def = slots[.usb] ?? slots[.wifi]
        return slots.values.filter { $0.routeOverride ?? ($0 === def) }
    }

    /// Returns false if no routed session accepted the frame (none routed, or all backed up).
    @discardableResult
    func sendVideo(_ payload: Data) -> Bool {
        lock.lock()
        let targets = routedLocked()
        var accepted = [Conn]()
        var message: String?
        for c in targets {
            if c.pendingVideo > Self.maxPendingVideo {
                droppedSinceLog += 1
                if Date().timeIntervalSince(lastDropLog) >= 5 {
                    message = "W dropped \(droppedSinceLog) video frame(s): send queue backed up"
                    droppedSinceLog = 0
                    lastDropLog = Date()
                }
            } else {
                c.pendingVideo += 1
                accepted.append(c)
            }
        }
        lock.unlock()
        if let m = message { onLog?(m) }
        for c in accepted {
            writeQueue.async { [weak self] in
                guard let self = self else { return }
                self.writeSealed(c, type: FrameType.video.rawValue, payload: payload)
                // pendingVideo belongs to this connection object only, so a replacement never skews it
                self.lock.lock()
                if c.pendingVideo > 0 { c.pendingVideo -= 1 }
                self.lock.unlock()
            }
        }
        return !accepted.isEmpty
    }

    /// `{"cmd":"route","video":bool}` for the session with generation `gen`.
    func setRoute(gen: UInt64, video: Bool) {
        lock.lock()
        var hit = false
        for c in slots.values where c.id == gen { c.routeOverride = video; hit = true }
        lock.unlock()
        if hit { onRoutingChanged?() }
    }

    /// Drops every connection (authenticated or not), e.g. on unpair.
    func closeAll() {
        lock.lock()
        for c in all.values where !c.retired { shutdown(c.fd, SHUT_RDWR) }
        lock.unlock()
    }

    // runs on writeQueue: seal and write under one (serial) writer, so counters follow wire order
    private func writeSealed(_ c: Conn, type: UInt8, payload: Data) {
        lock.lock(); let live = !c.retired; let ch = c.channel; lock.unlock()
        guard live, let channel = ch else { return }
        guard let sealed = try? channel.seal(innerType: type, payload: payload) else {
            lock.lock(); if !c.retired { shutdown(c.fd, SHUT_RDWR) }; lock.unlock()
            return
        }
        writeRaw(c, FrameCodec.encode(.sealed, sealed))
    }

    private func sendPlain(_ c: Conn, _ type: FrameType, _ payload: Data) {
        let bytes = FrameCodec.encode(type, payload)
        writeQueue.async { [weak self] in self?.writeRaw(c, bytes) }
    }

    // runs on writeQueue
    private func writeRaw(_ c: Conn, _ data: Data) {
        lock.lock(); let live = !c.retired; lock.unlock()
        guard live else { return }
        let ok: Bool = data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Bool in
            guard let base = raw.baseAddress else { return true }
            var sent = 0
            while sent < raw.count {
                let n = Darwin.write(c.fd, base + sent, raw.count - sent)
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
            if !c.retired { shutdown(c.fd, SHUT_RDWR) }
            lock.unlock()
        }
    }

    // MARK: reading

    private enum Phase { case awaitHello, awaitAuth1, awaitAuth3, sealed }

    private func readLoop(_ c: Conn) {
        var decoder = FrameDecoder()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        var phase = Phase.awaitHello
        var responder: ResponderHandshake?
        var channel: SealedChannel?

        outer: while true {
            let n = read(c.fd, &buffer, buffer.count)
            if n < 0 {
                if errno == EINTR { continue }
                break
            }
            if n == 0 { break }
            let frames: [Frame]
            do { frames = try decoder.feed(Data(buffer[0..<n])) } catch {
                onLog?("E protocol error: \(error)")
                break
            }
            for frame in frames {
                switch phase {
                case .awaitHello:
                    guard frame.type == FrameType.hello.rawValue, let h = try? Hello.parse(frame.payload),
                          h.proto == Hello.protocolVersion, h.role == "pc" else {
                        onLog?("W unsupported or out-of-order HELLO, closing")
                        break outer
                    }
                    phase = .awaitAuth1
                case .awaitAuth1:
                    guard frame.type == FrameType.auth.rawValue, let r = authority.makeResponder() else { break outer }
                    responder = r
                    guard let auth2 = try? r.readAuth1AndCreateAuth2(frame.payload) else { break outer }
                    sendPlain(c, .auth, auth2)
                    phase = .awaitAuth3
                case .awaitAuth3:
                    guard frame.type == FrameType.auth.rawValue, let r = responder,
                          (try? r.readAuth3(frame.payload)) != nil, let result = r.result else { break outer }
                    let ch = SealedChannel(keys: result.keys, role: .responder)
                    guard promote(c, channel: ch) else { break outer }
                    channel = ch
                    responder = nil
                    phase = .sealed
                case .sealed:
                    guard frame.type == FrameType.sealed.rawValue, let ch = channel,
                          let inner = try? ch.open(frame.payload) else {
                        onLog?("W sealed frame rejected, closing session")
                        break outer
                    }
                    if answerPings && inner.type == FrameType.ping.rawValue {
                        let recv = PhoneClock.nowUs()
                        if let sent = Payloads.parsePing(inner.payload) {
                            enqueue(c, FrameType.pong.rawValue, Payloads.pong(pcSendUs: sent, phoneRecvUs: recv))
                        }
                        continue
                    }
                    onFrame?(c.id, c.kind, inner)
                }
            }
        }

        responder?.dispose() // releases an unconsumed token claim
        lock.lock()
        let wasLive = c.authenticated && !c.retired
        c.retired = true
        if slots[c.kind] === c { slots[c.kind] = nil }
        if !c.authenticated { unauthenticated -= 1 }
        all[c.id] = nil
        lock.unlock()
        // Close on the write queue: earlier queued writes finish first and later ones see a retired connection.
        let fd = c.fd
        writeQueue.async { close(fd) }
        if wasLive {
            onSessionEnded?(c.id, c.kind)
            onRoutingChanged?()
        }
    }

    /// AUTH3 verified: queue the sealed INFO first, then publish the connection in its slot, replacing the
    /// previous session of the same kind. Returns false if the connection was already retired.
    private func promote(_ c: Conn, channel: SealedChannel) -> Bool {
        // unpaired while this handshake was in flight: never admit it
        guard authority.isPairedOrPending else { return false }
        lock.lock()
        if c.retired { lock.unlock(); return false }
        c.channel = channel
        c.authenticated = true
        unauthenticated -= 1
        let info = infoPayload
        // INFO is enqueued while holding the lock, before any other sender can see this connection
        writeQueue.async { [weak self] in self?.writeSealed(c, type: FrameType.info.rawValue, payload: info) }
        let old = slots[c.kind]
        slots[c.kind] = c
        if let old = old {
            old.retired = true
            shutdown(old.fd, SHUT_RDWR) // old reader sees EOF; it will not report a disconnect (replaced)
        }
        lock.unlock()
        if let old = old { onSessionEnded?(old.id, old.kind) }
        onSessionStarted?(c.id, c.kind)
        onRoutingChanged?()
        return true
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

    /// `allowed` = loopback or the device's own address (both are "USB"); everything else is a network peer.
    static func classifyPeer(_ fd: Int32) -> (allowed: Bool, description: String) {
        guard let peer = address(fd, peer: true) else { return (false, "unknown") }
        let local = address(fd, peer: false)?.bytes ?? []
        return (PeerAddress.isAllowed(family: peer.family, peer: peer.bytes, local: local),
                PeerAddress.describe(family: peer.family, bytes: peer.bytes))
    }
}

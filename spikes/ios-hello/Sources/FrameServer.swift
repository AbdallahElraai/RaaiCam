import Foundation
import Darwin

/// BSD-socket TCP server (NWListener does not accept connections arriving via
/// the usbmux/loopback route). Per connection: write one
/// [u32 big-endian length][JPEG bytes] packet (length 0 if no frame yet), then close.
final class FrameServer {
    private let port: UInt16
    private let grabber: CameraGrabber
    private let clientQueue = DispatchQueue(label: "raaicam.server.client", attributes: .concurrent)
    private var listenFD: Int32 = -1

    var onState: ((String) -> Void)?
    var onFrameServed: (() -> Void)?
    var onSendError: ((String) -> Void)?

    init(port: UInt16 = 47820, grabber: CameraGrabber) {
        self.port = port
        self.grabber = grabber
    }

    private static func errnoText() -> String { String(cString: strerror(errno)) }

    func start() {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { onState?("error: \(Self.errnoText())"); return }

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
            let msg = Self.errnoText()
            close(fd)
            onState?("error: \(msg)")
            return
        }
        guard listen(fd, 8) == 0 else {
            let msg = Self.errnoText()
            close(fd)
            onState?("error: \(msg)")
            return
        }
        listenFD = fd
        onState?("ready on \(port)")

        let thread = Thread { [weak self] in self?.acceptLoop(fd) }
        thread.name = "raaicam.accept"
        thread.start()
    }

    private func acceptLoop(_ fd: Int32) {
        while true {
            let client = accept(fd, nil, nil)
            if client < 0 {
                if errno == EINTR || errno == ECONNABORTED { continue }
                onState?("error: \(Self.errnoText())")
                return
            }
            clientQueue.async { [weak self] in self?.serve(client) }
        }
    }

    private func serve(_ client: Int32) {
        defer { close(client) }
        var one: Int32 = 1
        setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))

        let jpeg = grabber.latestJPEG() ?? Data()
        var len = UInt32(jpeg.count).bigEndian
        var packet = Data(bytes: &len, count: 4)
        packet.append(jpeg)

        let ok: Bool = packet.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Bool in
            guard let base = raw.baseAddress else { return false }
            var sent = 0
            while sent < raw.count {
                let n = write(client, base + sent, raw.count - sent)
                if n < 0 {
                    if errno == EINTR { continue }
                    onSendError?(Self.errnoText())
                    return false
                }
                sent += n
            }
            return true
        }
        if ok && !jpeg.isEmpty { onFrameServed?() }
    }
}

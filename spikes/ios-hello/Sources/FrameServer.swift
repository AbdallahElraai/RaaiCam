import Foundation
import Network

final class FrameServer {
    private let port: UInt16
    private let grabber: CameraGrabber
    private let queue = DispatchQueue(label: "raaicam.server")
    private var listener: NWListener?

    var onState: ((String) -> Void)?
    var onFrameServed: (() -> Void)?
    var onSendError: ((String) -> Void)?

    init(port: UInt16 = 47820, grabber: CameraGrabber) {
        self.port = port
        self.grabber = grabber
    }

    func start() {
        do {
            let l = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
            l.stateUpdateHandler = { [weak self] state in
                guard let self = self else { return }
                switch state {
                case .ready: self.onState?("ready on \(self.port)")
                case .failed(let e): self.onState?("error: \(e)")
                case .waiting(let e): self.onState?("waiting: \(e)")
                default: break
                }
            }
            l.newConnectionHandler = { [weak self] conn in self?.handle(conn) }
            l.start(queue: queue)
            listener = l
        } catch {
            onState?("error: \(error)")
        }
    }

    private func handle(_ conn: NWConnection) {
        conn.start(queue: queue)
        let jpeg = grabber.latestJPEG() ?? Data()
        var len = UInt32(jpeg.count).bigEndian
        var packet = Data(bytes: &len, count: 4)
        packet.append(jpeg)
        conn.send(content: packet, completion: .contentProcessed { [weak self] error in
            if let error = error {
                self?.onSendError?("\(error)")
            } else if !jpeg.isEmpty {
                self?.onFrameServed?()
            }
            conn.cancel()
        })
    }
}

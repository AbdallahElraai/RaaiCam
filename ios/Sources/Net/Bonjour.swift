import Foundation
import CryptoKit

/// Advertises `_raaicam._tcp` on port 47820 with TXT `v=2`, `id=<8 hex>`. The BSD socket in FrameServer
/// owns the port, so the service is published without listening (`publish(options: [])`).
/// Publish only while paired or a pairing token is pending. Main thread only.
final class BonjourPublisher {
    static let serviceType = "_raaicam._tcp."
    static let port: Int32 = 47820

    private var service: NetService?
    private var publishedId: String?

    /// First 4 bytes (8 hex) of SHA-256(phone static public key).
    static func idHex(forStaticPublic pub: Data) -> String {
        SHA256.hash(data: pub).prefix(4).map { String(format: "%02x", $0) }.joined()
    }

    static func serviceName(id: String) -> String { "RaaiCam-" + id }

    /// Idempotent: starts, keeps or stops the advertisement to match `staticPublic` (nil = stop).
    func update(staticPublic: Data?) {
        guard let pub = staticPublic else { stop(); return }
        let id = Self.idHex(forStaticPublic: pub)
        if publishedId == id, service != nil { return }
        stop()
        let svc = NetService(domain: "local.", type: Self.serviceType, name: Self.serviceName(id: id), port: Self.port)
        svc.setTXTRecord(NetService.data(fromTXTRecord: ["v": Data("2".utf8), "id": Data(id.utf8)]))
        svc.publish(options: [])
        service = svc
        publishedId = id
    }

    func stop() {
        service?.stop()
        service = nil
        publishedId = nil
    }
}

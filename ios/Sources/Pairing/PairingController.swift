import Foundation
import Combine

/// UI-side pairing flow: scanned QR -> pending token -> waiting for the PC -> "Paired with <PC>" (2 s).
/// Main thread only (store callbacks hop to main).
final class PairingController: ObservableObject {
    enum Phase: Equatable {
        case scanning(message: String?)
        case waiting(name: String, expiresAt: Date)
        case paired(name: String)
        case finished
    }

    static let successDisplay: TimeInterval = 2

    @Published private(set) var phase: Phase
    private let store: PairingStore
    private var lastScan: (value: String, at: Date)?

    init(store: PairingStore) {
        self.store = store
        self.phase = store.paired == nil ? .scanning(message: nil) : .finished
        store.addObserver { [weak self] in DispatchQueue.main.async { self?.refresh() } }
    }

    /// True while the pairing screen must be shown (unpaired, waiting, or the 2 s success banner).
    var screenActive: Bool { phase != .finished }

    /// A QR code was read by the scanner. The string is never logged or stored beyond the parsed token.
    func handleScanned(_ string: String) {
        guard case .scanning = phase else { return }
        // the camera reports the same code every frame: ignore repeats for 2 s
        if let last = lastScan, last.value == string, Date().timeIntervalSince(last.at) < 2 { return }
        lastScan = (string, Date())
        switch PairingURL.parse(string) {
        case .success(let token):
            do {
                try store.beginPairing(token)
                refresh()
            } catch {
                phase = .scanning(message: "Could not save pairing data. Try again.")
            }
        case .failure(let e):
            phase = .scanning(message: e.userMessage)
        }
    }

    /// Abandon the pending code and go back to the scanner.
    func rescan() {
        store.cancelPending()
        lastScan = nil
        phase = .scanning(message: nil)
    }

    func refresh() {
        let paired = store.paired
        let pending = store.pendingToken
        switch phase {
        case .finished:
            if paired == nil {
                if let p = pending { phase = Phase.waiting(name: p.name, expiresAt: p.expiresAt) }
                else { phase = Phase.scanning(message: nil) }
            }
        case .scanning:
            if let p = pending { phase = .waiting(name: p.name, expiresAt: p.expiresAt) }
            else if paired != nil { phase = .finished }
        case .waiting:
            if let pc = paired {
                phase = .paired(name: pc.label)
                DispatchQueue.main.asyncAfter(deadline: .now() + Self.successDisplay) { [weak self] in
                    guard let self = self, case .paired = self.phase else { return }
                    self.phase = .finished
                }
            } else if pending == nil {
                phase = .scanning(message: "That code expired. Show a new one on your PC.")
            }
        case .paired:
            if paired == nil { phase = .scanning(message: nil) }
        }
    }
}

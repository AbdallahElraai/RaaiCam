import SwiftUI

@main
struct RaaiCamApp: App {
    @StateObject private var session = PhoneSession()

    var body: some Scene {
        WindowGroup {
            ZStack {
                Color.black.ignoresSafeArea()
                Text(Self.describe(session.state))
                    .foregroundColor(.gray)
            }
            .onAppear {
                // Task 3 replaces this view. Skip the hardware/server start when hosting unit tests.
                if ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil { return }
                UIApplication.shared.isIdleTimerDisabled = true
                session.start()
            }
        }
    }

    static func describe(_ s: SessionState) -> String {
        switch s {
        case .waiting: return "Waiting for PC"
        case .connected: return "Connected"
        case .streaming(let w, let h): return "Streaming \(w)x\(h)"
        case .error(let m): return "Error: \(m)"
        }
    }
}

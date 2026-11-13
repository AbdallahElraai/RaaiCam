import SwiftUI

@main
struct RaaiCamApp: App {
    @StateObject private var session = PhoneSession()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ZStack {
                Color.black.ignoresSafeArea()
                VStack(spacing: 6) {
                    Text(Self.describe(session.state))
                    if !session.detail.isEmpty { Text(session.detail).font(.footnote) }
                }
                .foregroundColor(.gray)
            }
            .onAppear {
                // Task 3 replaces this view. Skip the hardware/server start when hosting unit tests.
                if Self.isRunningTests { return }
                UIApplication.shared.isIdleTimerDisabled = true
                session.start()
            }
            .onChange(of: scenePhase) { _, phase in
                if phase == .active && !Self.isRunningTests { session.restartIfNeeded() }
            }
        }
    }

    static var isRunningTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
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

import SwiftUI

@main
struct RaaiCamApp: App {
    @StateObject private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ContentView(model: model)
                .onAppear {
                    // Skip the hardware/server start when hosting unit tests.
                    if Self.isRunningTests { return }
                    model.scenePhaseChanged(scenePhase)
                    model.start()
                }
                .onChange(of: scenePhase) { _, phase in
                    if !Self.isRunningTests { model.scenePhaseChanged(phase) }
                }
        }
    }

    static var isRunningTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
    }
}

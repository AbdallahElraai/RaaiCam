import SwiftUI
import AVFoundation
import UIKit

final class SpikeModel: ObservableObject {
    @Published var cameraState = "unknown"
    @Published var listenerState = "starting"
    @Published var framesServed = 0
    @Published var cameraStatus = "not started"
    @Published var lastSendError = "none"

    private let grabber = CameraGrabber()
    private lazy var server = FrameServer(grabber: grabber)

    func start() {
        UIApplication.shared.isIdleTimerDisabled = true
        server.onState = { [weak self] s in DispatchQueue.main.async { self?.listenerState = s } }
        server.onFrameServed = { [weak self] in DispatchQueue.main.async { self?.framesServed += 1 } }
        server.onSendError = { [weak self] e in DispatchQueue.main.async { self?.lastSendError = e } }
        grabber.onState = { [weak self] s in DispatchQueue.main.async { self?.cameraStatus = s } }
        server.start()
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            DispatchQueue.main.async { self?.cameraState = granted ? "granted" : "denied" }
            if granted { self?.grabber.start() }
        }
    }
}

struct ContentView: View {
    @StateObject private var model = SpikeModel()

    var body: some View {
        VStack(spacing: 16) {
            Text("RaaiCam Spike").font(.largeTitle).bold()
            Text("Camera permission: \(model.cameraState)")
            Text("Camera: (model.cameraStatus)")
            Text("Listener: \(model.listenerState)")
            Text("Frames served: \(model.framesServed)")
            Text("Last send error: (model.lastSendError)")
        }
        .padding()
        .onAppear { model.start() }
    }
}

@main
struct SpikeApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}

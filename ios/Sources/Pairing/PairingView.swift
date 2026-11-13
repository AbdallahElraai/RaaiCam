import SwiftUI
import AVFoundation

/// Rear-camera QR scanner. Uses its own AVCaptureSession (never the streaming capture); it only exists
/// while the pairing screen shows the scanner and is stopped as soon as the view goes away.
struct QRScannerView: UIViewControllerRepresentable {
    let onCode: (String) -> Void
    let onAuthorizationResolved: () -> Void

    func makeUIViewController(context: Context) -> ScannerViewController {
        let vc = ScannerViewController()
        vc.onCode = onCode
        vc.onAuthorizationResolved = onAuthorizationResolved
        return vc
    }

    func updateUIViewController(_ vc: ScannerViewController, context: Context) {
        vc.onCode = onCode
    }

    static func dismantleUIViewController(_ vc: ScannerViewController, coordinator: ()) {
        vc.stop()
    }
}

final class ScannerViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCode: ((String) -> Void)?
    var onAuthorizationResolved: (() -> Void)?

    private let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "raaicam.scanner")
    private var preview: AVCaptureVideoPreviewLayer?
    private var configured = false
    private var wantsRunning = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill
        view.layer.addSublayer(layer)
        preview = layer
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preview?.frame = view.bounds
        if let conn = preview?.connection, conn.isVideoOrientationSupported,
           let o = view.window?.windowScene?.interfaceOrientation,
           let av = AVCaptureVideoOrientation(rawValue: o.rawValue) {
            conn.videoOrientation = av
        }
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        wantsRunning = true
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            DispatchQueue.main.async { self?.onAuthorizationResolved?() }
            guard granted, let self = self else { return }
            self.sessionQueue.async { self.configureAndRun() }
        }
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        stop()
    }

    func stop() {
        wantsRunning = false
        sessionQueue.async { [session] in
            if session.isRunning { session.stopRunning() }
        }
    }

    private func configureAndRun() {
        if !configured {
            guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back)
                    ?? AVCaptureDevice.default(for: .video),
                  let input = try? AVCaptureDeviceInput(device: device), session.canAddInput(input) else { return }
            session.beginConfiguration()
            session.addInput(input)
            let output = AVCaptureMetadataOutput()
            if session.canAddOutput(output) {
                session.addOutput(output)
                output.setMetadataObjectsDelegate(self, queue: .main)
                if output.availableMetadataObjectTypes.contains(.qr) { output.metadataObjectTypes = [.qr] }
            }
            session.commitConfiguration()
            configured = true
        }
        if wantsRunning, !session.isRunning { session.startRunning() }
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject],
                        from connection: AVCaptureConnection) {
        for obj in metadataObjects {
            if let code = obj as? AVMetadataMachineReadableCodeObject, code.type == .qr, let s = code.stringValue {
                onCode?(s)
                return
            }
        }
    }
}

struct PairingView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var pairing: PairingController

    init(model: AppModel) {
        self.model = model
        self.pairing = model.pairing
    }

    var body: some View {
        HStack(spacing: 36) {
            left
            right
        }
        .padding(32)
    }

    @ViewBuilder private var left: some View {
        switch pairing.phase {
        case .scanning:
            QRScannerView(onCode: { pairing.handleScanned($0) },
                          onAuthorizationResolved: { model.refreshAuthorization() })
                .frame(width: 220, height: 220)
                .clipShape(RoundedRectangle(cornerRadius: 16))
                .overlay(RoundedRectangle(cornerRadius: 16).stroke(Palette.muted.opacity(0.5), lineWidth: 2))
                .accessibilityLabel("Camera view for scanning the pairing code")
        case .waiting:
            ProgressView()
                .scaleEffect(2)
                .frame(width: 220, height: 220)
                .accessibilityHidden(true)
        case .paired:
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 96))
                .foregroundColor(.green)
                .frame(width: 220, height: 220)
                .accessibilityHidden(true)
        case .finished:
            EmptyView()
        }
    }

    @ViewBuilder private var right: some View {
        VStack(alignment: .leading, spacing: 10) {
            switch pairing.phase {
            case .scanning(let message):
                Text("Pair with your PC")
                    .font(.largeTitle.weight(.semibold))
                    .foregroundColor(.white)
                Text("Open RaaiCam on Windows and scan the code")
                    .font(.body)
                    .foregroundColor(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                if let message = message {
                    Text(message)
                        .font(.footnote)
                        .foregroundColor(Palette.amber)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityLabel(message)
                }
            case .waiting(let name, let expiresAt):
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    Text("Waiting for the PC… (expires in \(Self.countdown(until: expiresAt, now: context.date)))")
                        .font(.title2.weight(.semibold))
                        .foregroundColor(.white)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Text("Pairing with \(name). Keep this screen open.")
                    .font(.body)
                    .foregroundColor(Palette.muted)
                Button("Scan a different code") { pairing.rescan() }
                    .buttonStyle(.bordered)
                    .padding(.top, 8)
            case .paired(let name):
                Text("Paired with \(name)")
                    .font(.largeTitle.weight(.semibold))
                    .foregroundColor(.white)
            case .finished:
                EmptyView()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    static func countdown(until: Date, now: Date) -> String {
        let s = max(0, Int(until.timeIntervalSince(now).rounded(.up)))
        return String(format: "%d:%02d", s / 60, s % 60)
    }
}

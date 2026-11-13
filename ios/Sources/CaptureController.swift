import Foundation
import AVFoundation

enum CaptureEvent {
    case runtimeError(String)
    case interrupted(String)
    case interruptionEnded
}

/// Owns the AVCaptureSession. All configuration happens on `configQueue`;
/// sample buffers are delivered on `videoQueue`.
final class CaptureController: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    var onSample: ((CMSampleBuffer) -> Void)?
    var onLog: ((String) -> Void)?
    var onEvent: ((CaptureEvent) -> Void)?

    private let configQueue = DispatchQueue(label: "raaicam.capture.config")
    private let videoQueue = DispatchQueue(label: "raaicam.capture.video")
    private var session: AVCaptureSession?
    private var output: AVCaptureVideoDataOutput?
    private var input: AVCaptureDeviceInput?
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservation: NSKeyValueObservation?
    private var observers: [NSObjectProtocol] = []
    private(set) var currentLens: Lens = .wide
    private var fps = 30

    static func device(for lens: Lens) -> AVCaptureDevice? {
        switch lens {
        case .wide: return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back)
        case .ultra: return AVCaptureDevice.default(.builtInUltraWideCamera, for: .video, position: .back)
        }
    }

    /// Completion returns the lens actually in use and the frame rate actually applied.
    func start(width: Int, height: Int, fps: Int, lens: Lens,
               completion: @escaping (Result<(lens: Lens, fps: Int), Error>) -> Void) {
        configQueue.async { [self] in
            teardown()
            self.fps = fps
            var chosen = lens
            var found = Self.device(for: lens)
            if found == nil && lens == .ultra {
                onLog?("W ultra-wide camera not available, using wide")
                chosen = .wide
                found = Self.device(for: .wide)
            }
            guard let device = found else {
                completion(.failure(CaptureError.noCamera)); return
            }
            do {
                let input = try AVCaptureDeviceInput(device: device)
                let session = AVCaptureSession()
                let output = AVCaptureVideoDataOutput()
                session.beginConfiguration()
                guard session.canAddInput(input) else { throw CaptureError.cannotAddInput }
                session.addInput(input)
                // Preset is set after the input is attached so canSetSessionPreset reflects the device.
                var preset: AVCaptureSession.Preset = height >= 1080 ? .hd1920x1080 : .hd1280x720
                if !session.canSetSessionPreset(preset) {
                    onLog?("W preset \(preset.rawValue) unsupported, trying 1280x720")
                    preset = .hd1280x720
                    guard session.canSetSessionPreset(preset) else { throw CaptureError.presetUnsupported }
                }
                session.sessionPreset = preset
                output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String:
                                            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
                output.alwaysDiscardsLateVideoFrames = true
                output.setSampleBufferDelegate(self, queue: videoQueue)
                guard session.canAddOutput(output) else { throw CaptureError.cannotAddOutput }
                session.addOutput(output)
                session.commitConfiguration()

                self.session = session
                self.output = output
                self.input = input
                self.currentLens = chosen
                observe(session)
                let appliedFps = applyFrameRate(device)
                setupRotation(device)
                session.startRunning()
                completion(.success((chosen, appliedFps)))
            } catch {
                teardown()
                completion(.failure(error))
            }
        }
    }

    func switchLens(_ lens: Lens, completion: @escaping (Lens) -> Void) {
        configQueue.async { [self] in
            guard let session = session, let oldInput = input else { completion(currentLens); return }
            guard lens != currentLens else { completion(currentLens); return }
            guard let device = Self.device(for: lens) else {
                onLog?("W lens \(lens.rawValue) not available, staying on \(currentLens.rawValue)")
                completion(currentLens); return
            }
            do {
                let newInput = try AVCaptureDeviceInput(device: device)
                session.beginConfiguration()
                session.removeInput(oldInput)
                if session.canAddInput(newInput) {
                    session.addInput(newInput)
                    input = newInput
                    currentLens = lens
                } else {
                    session.addInput(oldInput)
                    onLog?("W could not switch lens")
                }
                session.commitConfiguration()
                if let dev = input?.device {
                    applyFrameRate(dev)
                    setupRotation(dev)
                }
            } catch {
                onLog?("W lens switch failed: \(error.localizedDescription)")
            }
            completion(currentLens)
        }
    }

    func stop() {
        configQueue.async { [self] in teardown() }
    }

    // MARK: private (configQueue only)

    private func teardown() {
        for o in observers { NotificationCenter.default.removeObserver(o) }
        observers.removeAll()
        rotationObservation?.invalidate()
        rotationObservation = nil
        rotationCoordinator = nil
        if let session = session {
            output?.setSampleBufferDelegate(nil, queue: nil)
            session.stopRunning()
        }
        session = nil
        output = nil
        input = nil
    }

    private func observe(_ session: AVCaptureSession) {
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) {
            [weak self] note in
            let err = note.userInfo?[AVCaptureSessionErrorKey] as? NSError
            self?.onEvent?(.runtimeError(err?.localizedDescription ?? "unknown error"))
        })
        observers.append(nc.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) {
            [weak self] note in
            let reason = (note.userInfo?[AVCaptureSessionInterruptionReasonKey] as? Int).map(String.init) ?? "?"
            self?.onEvent?(.interrupted("reason \(reason)"))
        })
        observers.append(nc.addObserver(forName: AVCaptureSession.interruptionEndedNotification, object: session, queue: nil) {
            [weak self] _ in
            self?.onEvent?(.interruptionEnded)
        })
    }

    /// Clamps to what the active format supports (30 preferred as the fallback) and returns the applied fps.
    @discardableResult
    private func applyFrameRate(_ device: AVCaptureDevice) -> Int {
        let ranges = device.activeFormat.videoSupportedFrameRateRanges
        func supports(_ f: Int) -> Bool {
            ranges.contains { Double(f) >= $0.minFrameRate && Double(f) <= $0.maxFrameRate }
        }
        var f = fps
        if !supports(f) {
            let fallback = supports(30) ? 30 : Int(ranges.map { $0.maxFrameRate }.max() ?? 30)
            onLog?("W fps \(f) not supported by the camera format, using \(fallback)")
            f = fallback
        }
        f = min(max(f, 1), 240)
        fps = f
        do {
            try device.lockForConfiguration()
            let d = CMTime(value: 1, timescale: CMTimeScale(f))
            device.activeVideoMinFrameDuration = d
            device.activeVideoMaxFrameDuration = d
            device.unlockForConfiguration()
        } catch {
            onLog?("W could not set frame rate: \(error.localizedDescription)")
        }
        return f
    }

    private func setupRotation(_ device: AVCaptureDevice) {
        rotationObservation?.invalidate()
        let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: nil)
        rotationCoordinator = coordinator
        apply(angle: coordinator.videoRotationAngleForHorizonLevelCapture)
        rotationObservation = coordinator.observe(\.videoRotationAngleForHorizonLevelCapture, options: [.new]) {
            [weak self] _, change in
            guard let self = self, let angle = change.newValue else { return }
            self.configQueue.async { self.apply(angle: angle) }
        }
    }

    private func apply(angle: CGFloat) {
        guard let connection = output?.connection(with: .video) else { return }
        if connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
    }

    // MARK: delegate (videoQueue)

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        onSample?(sampleBuffer)
    }
}

enum CaptureError: Error {
    case noCamera, cannotAddInput, cannotAddOutput, presetUnsupported
}

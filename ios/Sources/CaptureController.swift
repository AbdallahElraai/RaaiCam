import Foundation
import AVFoundation

/// Owns the AVCaptureSession. All configuration happens on `configQueue`;
/// sample buffers are delivered on `videoQueue`.
final class CaptureController: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    var onSample: ((CMSampleBuffer) -> Void)?
    var onLog: ((String) -> Void)?

    private let configQueue = DispatchQueue(label: "raaicam.capture.config")
    private let videoQueue = DispatchQueue(label: "raaicam.capture.video")
    private var session: AVCaptureSession?
    private var output: AVCaptureVideoDataOutput?
    private var input: AVCaptureDeviceInput?
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservation: NSKeyValueObservation?
    private(set) var currentLens: Lens = .wide
    private var fps = 30

    static func device(for lens: Lens) -> AVCaptureDevice? {
        switch lens {
        case .wide: return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back)
        case .ultra: return AVCaptureDevice.default(.builtInUltraWideCamera, for: .video, position: .back)
        }
    }

    func start(width: Int, height: Int, fps: Int, lens: Lens, completion: @escaping (Result<Lens, Error>) -> Void) {
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
                session.sessionPreset = height >= 1080 ? .hd1920x1080 : .hd1280x720
                guard session.canAddInput(input) else { throw CaptureError.cannotAddInput }
                session.addInput(input)
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
                applyFrameRate(device)
                setupRotation(device)
                session.startRunning()
                completion(.success(chosen))
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

    private func applyFrameRate(_ device: AVCaptureDevice) {
        do {
            try device.lockForConfiguration()
            let d = CMTime(value: 1, timescale: CMTimeScale(fps))
            device.activeVideoMinFrameDuration = d
            device.activeVideoMaxFrameDuration = d
            device.unlockForConfiguration()
        } catch {
            onLog?("W could not set frame rate: \(error.localizedDescription)")
        }
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
    case noCamera, cannotAddInput, cannotAddOutput
}

import AVFoundation
import CoreImage

final class CameraGrabber: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    private let session = AVCaptureSession()
    private let output = AVCaptureVideoDataOutput()
    private let queue = DispatchQueue(label: "raaicam.camera")
    private let lock = NSLock()
    private let ciContext = CIContext()
    private var latestBuffer: CVPixelBuffer?
    private var configured = false

    /// Reports camera setup outcome: "running" or "failed: <reason>".
    var onState: ((String) -> Void)?

    func start() {
        queue.async {
            if !self.configured { self.configure() }
            if self.configured && !self.session.isRunning {
                self.session.startRunning()
                self.onState?(self.session.isRunning ? "running" : "failed: session did not start")
            }
        }
    }

    private func fail(_ reason: String) {
        session.commitConfiguration()
        onState?("failed: \(reason)")
    }

    private func configure() {
        session.beginConfiguration()
        guard session.canSetSessionPreset(.hd1920x1080) else { return fail("1920x1080 preset unsupported") }
        session.sessionPreset = .hd1920x1080
        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) else {
            return fail("no back wide-angle camera")
        }
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            return fail(error.localizedDescription)
        }
        guard session.canAddInput(input) else { return fail("cannot add camera input") }
        session.addInput(input)
        output.alwaysDiscardsLateVideoFrames = true
        output.setSampleBufferDelegate(self, queue: queue)
        guard session.canAddOutput(output) else { return fail("cannot add video output") }
        session.addOutput(output)
        session.commitConfiguration()
        configured = true
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock()
        latestBuffer = pb
        lock.unlock()
    }

    func latestJPEG() -> Data? {
        lock.lock()
        let pb = latestBuffer
        lock.unlock()
        guard let pb = pb else { return nil }
        let image = CIImage(cvPixelBuffer: pb)
        let space = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
        return ciContext.jpegRepresentation(
            of: image,
            colorSpace: space,
            options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.8]
        )
    }
}

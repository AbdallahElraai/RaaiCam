import Foundation
import AVFoundation
import UIKit
import Combine

/// Orchestrates server, capture and encoder. Published state is only ever written on the main
/// thread (`publish`); everything else runs on `work`, the capture queue or the encoder callback.
final class PhoneSession: ObservableObject {
    @Published private(set) var state: SessionState = .waiting
    @Published private(set) var telemetry = TelemetrySnapshot()

    private let work = DispatchQueue(label: "raaicam.session")
    private let server = FrameServer()
    private let capture = CaptureController()

    // work-queue state
    private var started = false
    private var connected = false
    private var paused = false
    private var desired: StartCommand?
    private var snapshot = TelemetrySnapshot()
    private var telemetryTimer: DispatchSourceTimer?
    private var lastTelemetryAt = Date()

    // shared with capture/encoder threads (guarded by `lock`)
    private let lock = NSLock()
    private var encoder: H264Encoder?
    private var active: StartCommand?
    private var seq: UInt32 = 0
    private var capturedCount = 0
    private var encodedCount = 0
    private var announcedStreaming = false

    init() {}

    // MARK: public API

    func start() {
        work.async { [self] in
            guard !started else { return }
            started = true
            DispatchQueue.main.async { UIDevice.current.isBatteryMonitoringEnabled = true }
            capture.onLog = { [weak self] in self?.log($0) }
            capture.onSample = { [weak self] in self?.handleSample($0) }
            server.onLog = { [weak self] in self?.log($0) }
            server.onListening = { [weak self] in
                self?.log("I listening on 47820")
                self?.publish(state: .waiting)
            }
            server.onError = { [weak self] msg in
                self?.log("E server: \(msg)")
                self?.publish(state: .error(msg))
            }
            server.onClientConnected = { [weak self] in self?.work.async { self?.clientConnected() } }
            server.onClientDisconnected = { [weak self] in self?.work.async { self?.clientDisconnected() } }
            server.onFrame = { [weak self] frame in self?.receive(frame) }
            server.start()
        }
    }

    func setPaused(_ paused: Bool) {
        work.async { [self] in
            guard self.paused != paused else { return }
            self.paused = paused
            snapshot.paused = paused
            if paused {
                stopStreaming()
                if connected { publish(state: .connected) }
            } else if connected, let cmd = desired {
                beginCapture(cmd)
            }
            publishTelemetry()
            if connected { sendTelemetry() }
        }
    }

    func switchLens(_ lens: Lens) {
        work.async { [self] in applyLens(lens) }
    }

    // MARK: server events

    private func receive(_ frame: Frame) {
        if frame.type == FrameType.ping.rawValue {
            // answered right here on the reader thread: never waits behind video or the session queue
            let recv = PhoneClock.nowUs()
            if let sent = Payloads.parsePing(frame.payload) {
                server.send(.pong, Payloads.pong(pcSendUs: sent, phoneRecvUs: recv))
            }
            return
        }
        work.async { [self] in handle(frame) }
    }

    private func clientConnected() {
        connected = true
        stopStreaming()
        desired = nil
        server.send(.hello, Hello.phone(version: Self.appVersion, device: Self.deviceModel, os: Self.osString))
        publish(state: .connected)
        startTelemetryTimer()
        log("I client connected")
    }

    private func clientDisconnected() {
        connected = false
        desired = nil
        stopStreaming()
        telemetryTimer?.cancel()
        telemetryTimer = nil
        publish(state: .waiting)
        publishTelemetry()
    }

    private func handle(_ frame: Frame) {
        switch FrameType(rawValue: frame.type) {
        case .hello:
            if let h = try? Hello.parse(frame.payload), h.proto == 1 { return }
            log("E bad HELLO, closing")
            server.disconnectClient()
        case .cmd:
            do { try handle(command: Command.parse(frame.payload)) }
            catch { log("W bad command payload") }
        case .auth:
            break // reserved for 1C
        default:
            log("W unexpected frame type \(frame.type)")
        }
    }

    private func handle(command: Command) {
        switch command {
        case .start(let cmd):
            desired = cmd
            snapshot.lens = cmd.lens
            snapshot.bitrateKbps = cmd.bitrateKbps
            if paused { publishTelemetry(); return }
            beginCapture(cmd)
        case .stop:
            desired = nil
            stopStreaming()
            if connected { publish(state: .connected) }
            publishTelemetry()
        case .keyframe:
            lock.lock(); let enc = encoder; lock.unlock()
            enc?.forceKeyframe()
        case .bitrate(let kbps):
            desired?.bitrateKbps = kbps
            snapshot.bitrateKbps = kbps
            lock.lock(); let enc = encoder; active?.bitrateKbps = kbps; lock.unlock()
            enc?.setBitrate(kbps: kbps)
        case .lens(let lens):
            applyLens(lens)
        case .unknown(let name):
            log("W unknown cmd \(name)")
        }
    }

    private func applyLens(_ lens: Lens) {
        desired?.lens = lens
        lock.lock(); let isActive = active != nil; lock.unlock()
        guard isActive else { snapshot.lens = lens; publishTelemetry(); return }
        capture.switchLens(lens) { [weak self] actual in
            self?.work.async {
                guard let self = self else { return }
                self.snapshot.lens = actual
                self.lock.lock(); let enc = self.encoder; self.lock.unlock()
                enc?.forceKeyframe()
                self.publishTelemetry()
            }
        }
    }

    // MARK: capture / encode

    private func beginCapture(_ cmd: StartCommand) {
        stopStreaming()
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            self?.work.async {
                guard let self = self else { return }
                guard granted else {
                    self.log("E camera permission denied")
                    self.publish(state: .error("Camera permission denied"))
                    return
                }
                // a stop/disconnect/restart may have happened while the permission prompt was up
                guard self.connected, !self.paused, self.desired == cmd else { return }
                self.lock.lock()
                self.active = cmd
                self.seq = 0
                self.capturedCount = 0
                self.encodedCount = 0
                self.announcedStreaming = false
                self.lock.unlock()
                self.lastTelemetryAt = Date()
                self.capture.start(width: cmd.width, height: cmd.height, fps: cmd.fps, lens: cmd.lens) { result in
                    self.work.async {
                        switch result {
                        case .success(let lens):
                            self.snapshot.lens = lens
                            self.log("I capture started \(cmd.width)x\(cmd.height) \(lens.rawValue)")
                        case .failure(let error):
                            self.log("E capture failed: \(error)")
                            self.publish(state: .error("Capture failed"))
                        }
                    }
                }
            }
        }
    }

    private func stopStreaming() {
        capture.stop()
        lock.lock()
        let enc = encoder
        encoder = nil
        active = nil
        announcedStreaming = false
        lock.unlock()
        enc?.stop()
        snapshot.streaming = false
        snapshot.width = 0
        snapshot.height = 0
        snapshot.fpsCapture = 0
        snapshot.fpsEncode = 0
    }

    /// Capture queue. Encoder is created lazily from the first real buffer so it always matches
    /// the actual capture size.
    private func handleSample(_ sb: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sb) else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sb)
        lock.lock()
        capturedCount += 1
        guard let cfg = active else { lock.unlock(); return }
        var enc = encoder
        lock.unlock()

        if enc == nil {
            let w = CVPixelBufferGetWidth(pixelBuffer), h = CVPixelBufferGetHeight(pixelBuffer)
            if w != cfg.width || h != cfg.height {
                log("W capture size \(w)x\(h) differs from requested \(cfg.width)x\(cfg.height)")
            }
            do {
                let created = try H264Encoder(width: w, height: h, fps: cfg.fps, bitrateKbps: cfg.bitrateKbps,
                                              onFrame: { [weak self] in self?.handleEncoded($0) },
                                              onLog: { [weak self] in self?.log($0) })
                lock.lock()
                if active == cfg && encoder == nil { encoder = created; enc = created }
                else { enc = encoder }
                lock.unlock()
                if enc !== created { created.stop() }
            } catch {
                log("E encoder create failed: \(error)")
                return
            }
        }
        enc?.encode(pixelBuffer, pts: pts, duration: CMTime(value: 1, timescale: CMTimeScale(cfg.fps)))
    }

    /// VideoToolbox output thread.
    private func handleEncoded(_ frame: EncodedFrame) {
        lock.lock()
        encodedCount += 1
        let s = seq
        seq &+= 1
        let first = !announcedStreaming
        announcedStreaming = true
        let enc = encoder
        lock.unlock()

        let payload = Payloads.video(captureUs: PhoneClock.captureUs(fromHostPTS: frame.pts), seq: s,
                                     keyframe: frame.isKeyframe, accessUnit: frame.annexB)
        if !server.sendVideo(payload) {
            // a dropped frame breaks the reference chain: resync the PC with a fresh IDR
            enc?.forceKeyframe()
        }
        if first, let enc = enc {
            let w = enc.width, h = enc.height
            work.async { [self] in
                snapshot.streaming = true
                snapshot.width = w
                snapshot.height = h
                publish(state: .streaming(width: w, height: h))
                publishTelemetry()
            }
        }
    }

    // MARK: telemetry

    private func startTelemetryTimer() {
        telemetryTimer?.cancel()
        lastTelemetryAt = Date()
        let timer = DispatchSource.makeTimerSource(queue: work)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in self?.sendTelemetry() }
        timer.resume()
        telemetryTimer = timer
    }

    private func sendTelemetry() {
        let now = Date()
        let dt = max(now.timeIntervalSince(lastTelemetryAt), 0.001)
        lastTelemetryAt = now
        lock.lock()
        let cap = capturedCount, enc = encodedCount
        capturedCount = 0
        encodedCount = 0
        lock.unlock()
        snapshot.fpsCapture = (Double(cap) / dt * 10).rounded() / 10
        snapshot.fpsEncode = (Double(enc) / dt * 10).rounded() / 10
        snapshot.thermal = TelemetrySnapshot.thermalString(ProcessInfo.processInfo.thermalState)
        var level: Float = -1
        var chargeState = UIDevice.BatteryState.unknown
        DispatchQueue.main.sync {
            level = UIDevice.current.batteryLevel
            chargeState = UIDevice.current.batteryState
        }
        snapshot.battery = level < 0 ? 0 : Double(level)
        snapshot.charging = (chargeState == .charging || chargeState == .full)
        snapshot.paused = paused
        if connected { server.send(.telemetry, snapshot.jsonData()) }
        publishTelemetry()
    }

    // MARK: helpers

    private func publish(state: SessionState) {
        DispatchQueue.main.async { self.state = state }
    }

    private func publishTelemetry() {
        let copy = snapshot
        DispatchQueue.main.async { self.telemetry = copy }
    }

    /// `line` already carries its "I "/"W "/"E " prefix.
    private func log(_ line: String) {
        NSLog("RaaiCam %@", line)
        server.send(.log, Data(line.utf8))
    }

    private static var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0"
    }

    private static var deviceModel: String {
        var info = utsname()
        uname(&info)
        return withUnsafeBytes(of: &info.machine) { raw in
            String(decoding: raw.prefix(while: { $0 != 0 }), as: UTF8.self)
        }
    }

    private static var osString: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return "iOS \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
    }
}

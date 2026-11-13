import Foundation
import AVFoundation
import UIKit
import Combine

/// Orchestrates server, capture and encoder. Published state is only ever written on the main
/// thread (`publish`); everything else runs on `work`, the capture queue or the encoder callback.
final class PhoneSession: ObservableObject {
    @Published private(set) var state: SessionState = .waiting
    /// Extra human-readable context for `state` (e.g. a rejected non-loopback peer). Empty when none.
    @Published private(set) var detail: String = ""
    @Published private(set) var telemetry = TelemetrySnapshot()

    private let work = DispatchQueue(label: "raaicam.session")
    private let server = FrameServer()
    private let capture = CaptureController()

    // work-queue state
    private var started = false
    private var connected = false
    private var currentGen: UInt64 = 0
    private var paused = false
    private var desired: StartCommand?
    private var startToken: UInt64 = 0
    private var lastRuntimeRestart = Date.distantPast
    private var cameraDetailShown = false
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
    private var lastForcedKey = 0.0
    private var encoderRetryAt = 0.0

    private let logLock = NSLock()
    private var lastLogAt = [String: Date]()

    init() {}

    // MARK: public API

    func start() {
        work.async { [self] in
            guard !started else { return }
            started = true
            DispatchQueue.main.async { UIDevice.current.isBatteryMonitoringEnabled = true }
            capture.onLog = { [weak self] in self?.logThrottled($0) }
            capture.onSample = { [weak self] in self?.handleSample($0) }
            capture.onEvent = { [weak self] e in self?.work.async { self?.handle(captureEvent: e) } }
            server.onLog = { [weak self] in self?.logThrottled($0) }
            server.onListening = { [weak self] in
                self?.log("I listening on 47820")
                self?.work.async { self?.applyListenerEvent(.waiting) }
            }
            server.onError = { [weak self] msg in
                self?.log("E server: \(msg)")
                self?.work.async { self?.applyListenerEvent(.error(msg)) }
            }
            server.onRejectedPeer = { [weak self] peer in
                self?.log("W rejected non-loopback peer \(peer)")
                self?.publish(detail: "Rejected non-loopback peer \(peer)")
            }
            server.onClientConnected = { [weak self] gen in self?.work.async { self?.clientConnected(gen) } }
            server.onClientDisconnected = { [weak self] gen in self?.work.async { self?.clientDisconnected(gen) } }
            server.onFrame = { [weak self] frame in self?.work.async { self?.handle(frame) } }
            server.start()
        }
    }

    /// Call when the app becomes active again; re-opens the listener if it died while suspended.
    func restartIfNeeded() {
        server.restartIfNeeded()
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

    // MARK: server events (work queue)

    /// A listener (re)bind or failure says nothing about an attached client, so while one is attached
    /// the connected/streaming state must stand. Returns nil when the event is to be ignored.
    static func stateAfterListenerEvent(_ event: SessionState, clientAttached: Bool) -> SessionState? {
        clientAttached ? nil : event
    }

    private func applyListenerEvent(_ event: SessionState) {
        if let s = Self.stateAfterListenerEvent(event, clientAttached: connected) { publish(state: s) }
    }

    private func clientConnected(_ gen: UInt64) {
        currentGen = gen
        connected = true
        stopStreaming()
        desired = nil
        server.send(.hello, Hello.phone(version: Self.appVersion, device: Self.deviceModel, os: Self.osString))
        publish(state: .connected)
        publish(detail: "")
        startTelemetryTimer()
        log("I client connected")
    }

    private func clientDisconnected(_ gen: UInt64) {
        // A disconnect from a client that was already replaced must not touch the live one.
        guard gen == currentGen else { return }
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
        case .start(let raw):
            let (cmd, warnings) = raw.sanitized()
            warnings.forEach { log($0) }
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
        case .bitrate(let raw):
            let kbps = StartCommand.clampKbps(raw)
            if kbps != raw { log("W bitrate \(raw) kbps clamped to \(kbps)") }
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

    private func handle(captureEvent: CaptureEvent) {
        switch captureEvent {
        case .runtimeError(let msg):
            log("E camera runtime error: \(msg)")
            publish(detail: "Camera error")
            cameraDetailShown = true
            if connected, !paused, let d = desired, Date().timeIntervalSince(lastRuntimeRestart) > 5 {
                lastRuntimeRestart = Date()
                work.asyncAfter(deadline: .now() + 1) { [self] in
                    if connected, !paused, desired == d { beginCapture(d) }
                }
            }
        case .interrupted(let reason):
            log("W camera interrupted (\(reason))")
            publish(detail: "Camera interrupted")
            cameraDetailShown = true
        case .interruptionEnded:
            log("I camera interruption ended")
            publish(detail: "")
            cameraDetailShown = false
            if connected, !paused, let d = desired { beginCapture(d) }
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
        startToken &+= 1
        let token = startToken
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            self?.work.async {
                guard let self = self else { return }
                // A newer start, a stop or a disconnect while the prompt was up supersedes this one.
                guard token == self.startToken else { return }
                guard granted else {
                    self.log("E camera permission denied")
                    self.publish(state: .error("Camera permission denied"))
                    return
                }
                // Re-evaluate after the prompt: bitrate/lens changes made meanwhile are picked up from
                // `desired`; only a size or fps change (a different start) aborts this one.
                guard self.connected, !self.paused, let d = self.desired,
                      d.width == cmd.width, d.height == cmd.height, d.fps == cmd.fps else { return }
                self.lock.lock()
                self.active = d
                self.seq = 0
                self.capturedCount = 0
                self.encodedCount = 0
                self.announcedStreaming = false
                self.lock.unlock()
                self.lastTelemetryAt = Date()
                self.capture.start(width: d.width, height: d.height, fps: d.fps, lens: d.lens) { result in
                    self.work.async {
                        switch result {
                        case .success(let started):
                            self.snapshot.lens = started.lens
                            // a successful (re)start clears a stale "Camera error"/"interrupted" note
                            if self.cameraDetailShown { self.cameraDetailShown = false; self.publish(detail: "") }
                            self.lock.lock(); self.active?.fps = started.fps; self.lock.unlock()
                            self.log("I capture started \(d.width)x\(d.height) \(started.lens.rawValue) \(started.fps)fps")
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
        startToken &+= 1 // cancels a start that is waiting on the permission prompt
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

    /// Capture queue. The encoder is created lazily from the first real buffer and recreated whenever
    /// the buffer size changes, so it always matches the actual frame size.
    private func handleSample(_ sb: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sb) else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sb)
        let w = CVPixelBufferGetWidth(pixelBuffer), h = CVPixelBufferGetHeight(pixelBuffer)
        lock.lock()
        capturedCount += 1
        guard let cfg = active else { lock.unlock(); return }
        var enc = encoder
        let retryAt = encoderRetryAt
        lock.unlock()

        if enc == nil || enc!.width != w || enc!.height != h {
            if ProcessInfo.processInfo.systemUptime < retryAt { return }
            if enc != nil {
                logThrottled("W capture size changed to \(w)x\(h), recreating encoder")
            } else if w != cfg.width || h != cfg.height {
                logThrottled("W capture size \(w)x\(h) differs from requested \(cfg.width)x\(cfg.height)")
            }
            do {
                let created = try H264Encoder(width: w, height: h, fps: cfg.fps, bitrateKbps: cfg.bitrateKbps,
                                              onFrame: { [weak self] in self?.handleEncoded($0) },
                                              onLog: { [weak self] in self?.logThrottled($0) })
                lock.lock()
                let old = encoder
                let keep = active != nil
                if keep { encoder = created; announcedStreaming = false }
                lock.unlock()
                old?.stop()
                guard keep else { created.stop(); return }
                enc = created
            } catch {
                lock.lock(); encoderRetryAt = ProcessInfo.processInfo.systemUptime + 1; lock.unlock()
                logThrottled("E encoder create failed: \(error)")
                return
            }
        }
        enc?.encode(pixelBuffer, pts: pts, duration: CMTime(value: 1, timescale: CMTimeScale(max(1, cfg.fps))))
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
            // a dropped frame breaks the reference chain: resync the PC with a fresh IDR (max 1/s)
            let now = ProcessInfo.processInfo.systemUptime
            lock.lock()
            let allowed = now - lastForcedKey >= 1
            if allowed { lastForcedKey = now }
            lock.unlock()
            if allowed { enc?.forceKeyframe() }
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

    private func publish(detail: String) {
        DispatchQueue.main.async { self.detail = detail }
    }

    private func publishTelemetry() {
        let copy = snapshot
        DispatchQueue.main.async { self.telemetry = copy }
    }

    /// `line` already carries its "I "/"W "/"E " prefix. Newlines would break the one-line LOG contract.
    private func log(_ line: String) {
        let clean = line.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
        NSLog("RaaiCam %@", clean)
        server.send(.log, Data(clean.utf8))
    }

    /// For anything that can fire per frame: at most one identical-prefix line per 5 s.
    private func logThrottled(_ line: String) {
        let key = String(line.prefix(48))
        let now = Date()
        logLock.lock()
        if let last = lastLogAt[key], now.timeIntervalSince(last) < 5 { logLock.unlock(); return }
        lastLogAt[key] = now
        logLock.unlock()
        log(line)
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

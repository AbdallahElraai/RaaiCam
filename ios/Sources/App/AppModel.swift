import Foundation
import AVFoundation
import Combine
import SwiftUI
import UIKit

enum ThermalLevel: Equatable { case serious, critical }

enum WarningKind: Equatable {
    case cameraPermission
    case thermal(ThermalLevel)
    case disconnected
    case notCharging
}

enum Screen: Equatable {
    case ready
    case streaming
    case warning(WarningKind)
    case pairing
}

/// Brightness seam so the dimmer is testable without a real screen.
protocol BrightnessControlling: AnyObject {
    var brightness: CGFloat { get set }
}

final class ScreenBrightness: BrightnessControlling {
    var brightness: CGFloat {
        get { UIScreen.main.brightness }
        set { UIScreen.main.brightness = newValue }
    }
}

/// Dims the screen and remembers the user's brightness so it can always be put back.
final class Dimmer {
    static let dimLevel: CGFloat = 0.05
    private let control: BrightnessControlling
    private var original: CGFloat?
    var isDimmed: Bool { original != nil }

    init(control: BrightnessControlling) { self.control = control }

    func dim() {
        guard original == nil else { return }
        original = control.brightness
        control.brightness = Self.dimLevel
    }

    func restore() {
        guard let o = original else { return }
        control.brightness = o
        original = nil
    }
}

/// App-level UI state. The screen decision is a pure function so it can be unit-tested.
final class AppModel: ObservableObject {
    static let idleDimDelay: TimeInterval = 10

    let session: PhoneSession
    let store: PairingStore
    let pairing: PairingController
    @Published private(set) var isPaired = false
    @Published private(set) var links = Set<LinkKind>()
    @Published private(set) var pairingActive = false
    @Published private(set) var state: SessionState = .waiting
    @Published private(set) var telemetry = TelemetrySnapshot()
    @Published private(set) var detail: String = ""
    @Published private(set) var screen: Screen = .ready
    @Published private(set) var advisory: WarningKind?
    @Published private(set) var isDimmed = false
    @Published private(set) var cameraAuthorized = true
    @Published var showControls = false { didSet { if showControls != oldValue { refreshDimming() } } }

    let hasUltraWide: Bool
    private let dimmer: Dimmer
    private var dimTimer: Timer?
    private var appActive = false
    private var bag = Set<AnyCancellable>()

    init(store: PairingStore = PairingStore(), session: PhoneSession? = nil,
         brightness: BrightnessControlling = ScreenBrightness()) {
        self.store = store
        self.session = session ?? PhoneSession(store: store)
        let controller = PairingController(store: store)
        self.pairing = controller
        self.isPaired = store.paired != nil
        self.pairingActive = controller.screenActive
        self.dimmer = Dimmer(control: brightness)
        self.hasUltraWide = AVCaptureDevice.default(.builtInUltraWideCamera, for: .video, position: .back) != nil
        pairing.$phase.sink { [weak self] _ in
            guard let self = self else { return }
            self.isPaired = self.store.paired != nil
            self.pairingActive = self.pairing.screenActive
            self.recompute()
        }.store(in: &bag)
        self.session.$links.sink { [weak self] in self?.links = $0 }.store(in: &bag)
        self.session.$state.sink { [weak self] in self?.state = $0; self?.recompute() }.store(in: &bag)
        self.session.$telemetry.sink { [weak self] in self?.telemetry = $0; self?.recompute() }.store(in: &bag)
        self.session.$detail.sink { [weak self] in self?.detail = $0 }.store(in: &bag)
        NotificationCenter.default.publisher(for: UIApplication.willTerminateNotification)
            .sink { [weak self] _ in self?.dimmer.restore() }.store(in: &bag)
        refreshAuthorization()
    }

    deinit { dimmer.restore() }

    // MARK: pure decisions

    /// Precedence: camera permission denied > thermal critical > session error > thermal serious >
    /// (streaming only) not charging with battery under 30%. While streaming, serious thermal and the
    /// battery condition do not replace the Streaming screen; see `advisory(for:...)`.
    static func screen(for state: SessionState, telemetry: TelemetrySnapshot, cameraAuthorized: Bool,
                       pairingActive: Bool = false) -> Screen {
        if !cameraAuthorized { return .warning(.cameraPermission) }
        if pairingActive { return .pairing }
        if telemetry.thermal == "critical" { return .warning(.thermal(.critical)) }
        if case .error = state { return .warning(.disconnected) }
        let live = isStreaming(state)
        if telemetry.thermal == "serious" { return live ? .streaming : .warning(.thermal(.serious)) }
        return live ? .streaming : .ready
    }

    /// The amber line shown on the Streaming screen (nil when there is nothing to warn about).
    static func advisory(for state: SessionState, telemetry: TelemetrySnapshot, cameraAuthorized: Bool) -> WarningKind? {
        guard isStreaming(state), cameraAuthorized else { return nil }
        switch telemetry.thermal {
        case "critical": return .thermal(.critical)
        case "serious": return .thermal(.serious)
        default: break
        }
        // battery 0 means "level unknown" (UIDevice reports -1), so it is not treated as low
        if !telemetry.charging, telemetry.battery > 0, telemetry.battery < 0.30 { return .notCharging }
        return nil
    }

    private static func isStreaming(_ s: SessionState) -> Bool {
        if case .streaming = s { return true }
        return false
    }

    private func recompute() {
        let s = Self.screen(for: state, telemetry: telemetry, cameraAuthorized: cameraAuthorized, pairingActive: pairingActive)
        let a = Self.advisory(for: state, telemetry: telemetry, cameraAuthorized: cameraAuthorized)
        if s != screen { screen = s }
        if a != advisory { advisory = a }
        refreshDimming()
    }

    // MARK: lifecycle / input

    func start() {
        session.start()
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        switch phase {
        case .active:
            appActive = true
            UIApplication.shared.isIdleTimerDisabled = true
            refreshAuthorization()
            session.restartIfNeeded()
            refreshDimming()
        default:
            appActive = false
            refreshDimming() // not eligible: restores brightness and cancels the timer
        }
    }

    /// Any touch: wake the screen and restart the idle countdown.
    func userTouched() {
        wake()
        refreshDimming()
    }

    func tapped() {
        userTouched()
        if screen == .pairing { return }
        showControls = true
    }

    func setPaused(_ paused: Bool) { session.setPaused(paused) }
    func switchLens(_ lens: Lens) { session.switchLens(lens) }
    func unpair() { session.unpair() }

    func refreshAuthorization() {
        let status = AVCaptureDevice.authorizationStatus(for: .video)
        // notDetermined: the prompt comes with the first start
        let ok = !(status == .denied || status == .restricted)
        if ok != cameraAuthorized { cameraAuthorized = ok }
        recompute()
    }

    // MARK: dimming

    private var dimEligible: Bool {
        guard appActive, !showControls else { return false }
        switch screen {
        case .ready, .streaming: return true
        case .pairing: return false
        case .warning: return false
        }
    }

    private func wake() {
        dimTimer?.invalidate()
        dimTimer = nil
        dimmer.restore()
        if isDimmed { isDimmed = false }
    }

    private func refreshDimming() {
        guard dimEligible else {
            wake()
            return
        }
        if dimmer.isDimmed || dimTimer != nil { return }
        let t = Timer(timeInterval: Self.idleDimDelay, repeats: false) { [weak self] _ in
            guard let self = self, self.dimEligible else { return }
            self.dimTimer = nil
            self.dimmer.dim()
            self.isDimmed = true
        }
        RunLoop.main.add(t, forMode: .common)
        dimTimer = t
    }
}

import XCTest
@testable import RaaiCam

final class AppModelTests: XCTestCase {
    private func tele(thermal: String = "nominal", battery: Double = 0.8, charging: Bool = true) -> TelemetrySnapshot {
        var t = TelemetrySnapshot()
        t.thermal = thermal
        t.battery = battery
        t.charging = charging
        return t
    }
    private let streaming = SessionState.streaming(width: 1920, height: 1080)

    func testReadyWhenWaiting() {
        XCTAssertEqual(AppModel.screen(for: .waiting, telemetry: tele(), cameraAuthorized: true), .ready)
        XCTAssertEqual(AppModel.screen(for: .connected, telemetry: tele(), cameraAuthorized: true), .ready)
    }

    func testStreamingWhenStreaming() {
        XCTAssertEqual(AppModel.screen(for: streaming, telemetry: tele(), cameraAuthorized: true), .streaming)
        XCTAssertNil(AppModel.advisory(for: streaming, telemetry: tele(), cameraAuthorized: true))
    }

    func testWarningShownForSeriousThermal() {
        // not live: full warning screen
        XCTAssertEqual(AppModel.screen(for: .connected, telemetry: tele(thermal: "serious"), cameraAuthorized: true),
                       .warning(.thermal(.serious)))
        // live: stays on the Streaming screen with an amber advisory line
        XCTAssertEqual(AppModel.screen(for: streaming, telemetry: tele(thermal: "serious"), cameraAuthorized: true), .streaming)
        XCTAssertEqual(AppModel.advisory(for: streaming, telemetry: tele(thermal: "serious"), cameraAuthorized: true),
                       .thermal(.serious))
    }

    func testCriticalThermalWins() {
        let t = tele(thermal: "critical", battery: 0.1, charging: false)
        XCTAssertEqual(AppModel.screen(for: streaming, telemetry: t, cameraAuthorized: true), .warning(.thermal(.critical)))
        XCTAssertEqual(AppModel.screen(for: .waiting, telemetry: t, cameraAuthorized: true), .warning(.thermal(.critical)))
        // but camera permission beats it
        XCTAssertEqual(AppModel.screen(for: streaming, telemetry: t, cameraAuthorized: false), .warning(.cameraPermission))
    }

    func testNotChargingLowBatteryWarning() {
        let low = tele(battery: 0.2, charging: false)
        XCTAssertEqual(AppModel.screen(for: streaming, telemetry: low, cameraAuthorized: true), .streaming)
        XCTAssertEqual(AppModel.advisory(for: streaming, telemetry: low, cameraAuthorized: true), .notCharging)
        // serious thermal outranks the battery line
        XCTAssertEqual(AppModel.advisory(for: streaming, telemetry: tele(thermal: "serious", battery: 0.2, charging: false),
                                         cameraAuthorized: true), .thermal(.serious))
        // not while idle, charging, at/above 30%, or with an unknown (0) level
        XCTAssertNil(AppModel.advisory(for: .waiting, telemetry: low, cameraAuthorized: true))
        XCTAssertNil(AppModel.advisory(for: streaming, telemetry: tele(battery: 0.2, charging: true), cameraAuthorized: true))
        XCTAssertNil(AppModel.advisory(for: streaming, telemetry: tele(battery: 0.30, charging: false), cameraAuthorized: true))
        XCTAssertNil(AppModel.advisory(for: streaming, telemetry: tele(battery: 0, charging: false), cameraAuthorized: true))
    }

    func testPermissionDeniedWarning() {
        XCTAssertEqual(AppModel.screen(for: .waiting, telemetry: tele(), cameraAuthorized: false), .warning(.cameraPermission))
        XCTAssertEqual(AppModel.screen(for: .error("x"), telemetry: tele(thermal: "serious"), cameraAuthorized: false),
                       .warning(.cameraPermission))
    }

    func testDisconnectedOnlyForSessionError() {
        XCTAssertEqual(AppModel.screen(for: .error("Listener failed"), telemetry: tele(), cameraAuthorized: true),
                       .warning(.disconnected))
        XCTAssertEqual(AppModel.screen(for: .waiting, telemetry: tele(), cameraAuthorized: true), .ready)
    }

    // MARK: Dimmer

    private final class FakeBrightness: BrightnessControlling { var brightness: CGFloat = 0.7 }

    func testDimmerDimsAndRestoresOriginal() {
        let fake = FakeBrightness()
        let d = Dimmer(control: fake)
        d.dim()
        XCTAssertEqual(fake.brightness, 0.05, accuracy: 0.0001)
        XCTAssertTrue(d.isDimmed)
        d.dim() // must not capture the dimmed value as "original"
        d.restore()
        XCTAssertEqual(fake.brightness, 0.7, accuracy: 0.0001)
        XCTAssertFalse(d.isDimmed)
        d.restore() // idempotent
        XCTAssertEqual(fake.brightness, 0.7, accuracy: 0.0001)
    }

    // MARK: carried defect (a): listener events while a client is attached

    func testListenerEventsIgnoredWhileClientAttached() {
        XCTAssertNil(PhoneSession.stateAfterListenerEvent(.waiting, clientAttached: true))
        XCTAssertNil(PhoneSession.stateAfterListenerEvent(.error("rebind"), clientAttached: true))
        XCTAssertEqual(PhoneSession.stateAfterListenerEvent(.waiting, clientAttached: false), .waiting)
        XCTAssertEqual(PhoneSession.stateAfterListenerEvent(.error("rebind"), clientAttached: false), .error("rebind"))
    }
}

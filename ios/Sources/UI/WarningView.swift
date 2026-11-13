import SwiftUI

struct WarningCopy {
    let symbol: String
    let title: String
    let advice: String

    static func make(_ kind: WarningKind) -> WarningCopy {
        switch kind {
        case .cameraPermission:
            return .init(symbol: "video.slash.fill", title: "Camera access is off",
                         advice: "Turn on Camera for RaaiCam in Settings.")
        case .thermal(.serious):
            return .init(symbol: "thermometer.high", title: "Phone is warm",
                         advice: "Remove the case or point a fan at the phone.")
        case .thermal(.critical):
            return .init(symbol: "thermometer.sun.fill", title: "Phone is too hot",
                         advice: "Streaming may stop. Let the phone cool down.")
        case .disconnected:
            return .init(symbol: "cable.connector.slash", title: "Connection problem",
                         advice: "Replug the USB cable and reopen RaaiCam.")
        case .notCharging:
            return .init(symbol: "battery.25", title: "Not charging",
                         advice: "Battery is low. Check the USB cable.")
        }
    }
}

struct WarningView: View {
    let kind: WarningKind
    @ObservedObject var model: AppModel

    var body: some View {
        let c = WarningCopy.make(kind)
        HStack(spacing: 32) {
            Image(systemName: c.symbol)
                .font(.system(size: 64))
                .foregroundColor(Palette.amber)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 8) {
                Text(c.title).font(.largeTitle.weight(.semibold)).foregroundColor(.white)
                Text(c.advice).font(.body).foregroundColor(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                Pill(systemImage: "iphone",
                     text: "\(Format.battery(model.telemetry)) · \(Format.thermal(model.telemetry.thermal))")
                    .padding(.top, 12)
            }
        }
        .padding(32)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(c.title). \(c.advice)")
    }
}

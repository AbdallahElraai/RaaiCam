import SwiftUI

struct ReadyView: View {
    @ObservedObject var model: AppModel

    private var usbText: String {
        switch model.state {
        case .waiting: return "USB · waiting"
        default: return "USB · connected"
        }
    }

    var body: some View {
        HStack(spacing: 32) {
            Image(systemName: "camera.aperture")
                .font(.system(size: 64))
                .foregroundColor(Palette.muted.opacity(0.6))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 8) {
                Text(model.telemetry.paused ? "Paused" : "Ready")
                    .font(.largeTitle.weight(.semibold))
                    .foregroundColor(Palette.muted)
                Text(model.telemetry.paused ? "Camera is paused. Tap to resume it." : "Camera starts when a meeting app needs it")
                    .font(.body)
                    .foregroundColor(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 10) { pills }
                    VStack(alignment: .leading, spacing: 8) { pills }
                }
                .padding(.top, 12)
            }
        }
        .padding(32)
    }

    @ViewBuilder private var pills: some View {
        Pill(systemImage: "cable.connector", text: usbText)
        Pill(systemImage: "battery.100", text: "\(Format.battery(model.telemetry)) · \(Format.thermal(model.telemetry.thermal))")
    }
}

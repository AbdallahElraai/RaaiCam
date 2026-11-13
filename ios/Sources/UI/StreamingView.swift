import SwiftUI

struct StreamingView: View {
    @ObservedObject var model: AppModel

    private var statusText: String {
        let h = model.telemetry.height
        let res = h > 0 ? "\(h)p" : "Live"
        return "Live · \(res) · \(Format.thermal(model.telemetry.thermal))"
    }

    var body: some View {
        VStack(spacing: 6) {
            Spacer()
            if let kind = model.advisory {
                let c = WarningCopy.make(kind)
                Label(c.advice, systemImage: c.symbol)
                    .font(.footnote)
                    .foregroundColor(Palette.amber)
                    .padding(.bottom, 8)
                    .accessibilityLabel("\(c.title). \(c.advice)")
            }
            HStack(spacing: 8) {
                Circle().fill(Palette.red).frame(width: 7, height: 7).accessibilityHidden(true)
                Text(statusText).font(.footnote).foregroundColor(Color(white: 0.73))
            }
            .opacity(0.55)
            .accessibilityElement(children: .combine)
            .accessibilityLabel(statusText.replacingOccurrences(of: "·", with: ","))
            Text("Tap to show details")
                .font(.caption)
                .foregroundColor(Palette.muted)
                .opacity(0.4)
        }
        .padding(.bottom, 20)
    }
}

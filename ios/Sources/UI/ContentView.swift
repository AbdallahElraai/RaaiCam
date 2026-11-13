import SwiftUI

enum Palette {
    static let background = Color(red: 0.02, green: 0.02, blue: 0.024)
    static let pill = Color(red: 0.11, green: 0.114, blue: 0.133)
    static let muted = Color(red: 0.557, green: 0.557, blue: 0.576)
    static let amber = Color(red: 1.0, green: 0.698, blue: 0.141)
    static let red = Color(red: 0.898, green: 0.282, blue: 0.302)
}

struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ZStack {
            (model.screen == .streaming ? Color.black : Palette.background).ignoresSafeArea()
            switch model.screen {
            case .ready: ReadyView(model: model)
            case .streaming: StreamingView(model: model)
            case .warning(let kind): WarningView(kind: kind, model: model)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { model.tapped() }
        .accessibilityAction(named: "Show camera controls") { model.tapped() }
        .sheet(isPresented: $model.showControls) { QuickControls(model: model) }
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
        .preferredColorScheme(.dark)
    }
}

struct Pill: View {
    let systemImage: String
    let text: String
    var body: some View {
        Label(text, systemImage: systemImage)
            .font(.footnote)
            .foregroundColor(.white)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Palette.pill, in: Capsule())
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(text.replacingOccurrences(of: "·", with: ","))
    }
}

enum Format {
    static func thermal(_ s: String) -> String { s.prefix(1).uppercased() + s.dropFirst() }
    static func battery(_ t: TelemetrySnapshot) -> String {
        t.battery > 0 ? "\(Int((t.battery * 100).rounded()))%" : "Battery --"
    }
}

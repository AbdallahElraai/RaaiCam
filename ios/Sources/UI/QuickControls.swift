import SwiftUI

struct QuickControls: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Pause camera", isOn: Binding(
                        get: { model.telemetry.paused },
                        set: { model.setPaused($0) }))
                        .accessibilityHint("Stops the camera until you turn this off")
                }
                Section("Switch lens") {
                    HStack(spacing: 12) {
                        lensButton("Wide", .wide, enabled: true)
                        lensButton("Ultra-wide", .ultra, enabled: model.hasUltraWide)
                    }
                    .buttonStyle(.borderless)
                }
                if !model.detail.isEmpty {
                    Section("Diagnostics") {
                        Text(model.detail).font(.footnote).foregroundColor(.secondary)
                            .accessibilityLabel("Diagnostics: \(model.detail)")
                    }
                }
            }
            .navigationTitle("Controls")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Close") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }

    private func lensButton(_ title: String, _ lens: Lens, enabled: Bool) -> some View {
        let selected = model.telemetry.lens == lens
        return Button { model.switchLens(lens) } label: {
            Text(title)
                .font(.body.weight(selected ? .semibold : .regular))
                .frame(maxWidth: .infinity, minHeight: 44)
                .background(selected ? Color.accentColor.opacity(0.35) : Palette.pill, in: RoundedRectangle(cornerRadius: 10))
        }
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.4)
        .accessibilityLabel("\(title) lens")
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityHint(enabled ? "" : "Not available on this iPhone")
    }
}

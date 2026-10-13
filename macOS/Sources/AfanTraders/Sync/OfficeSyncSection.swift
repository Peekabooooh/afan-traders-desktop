import SwiftUI

/// Settings → Office Network Sync.
struct OfficeSyncSection: View {
    @ObservedObject var lan: LANSync
    @State private var code = ""
    @State private var showCode = false

    var body: some View {
        Section {
            LabeledContent("Status") {
                HStack(spacing: 8) {
                    Image(systemName: icon).foregroundStyle(color)
                    Text(lan.statusText).foregroundStyle(color)
                }
            }
            if lan.enabled {
                ForEach(lan.peers.values.sorted { $0.name < $1.name }) { p in
                    LabeledContent(p.name) {
                        HStack(spacing: 6) {
                            Circle().fill(p.connected ? Theme.green : Color.secondary.opacity(0.4)).frame(width: 8, height: 8)
                            Text(p.connected ? (p.lastSync.map { "Synced \($0.formatted(.relative(presentation: .named)))" } ?? "Connected")
                                             : "Not reachable right now")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                ForEach(lan.problems.prefix(3), id: \.self) { Text($0).font(.callout).foregroundStyle(Theme.orange) }
                LabeledContent("") {
                    Button("Turn Off", role: .destructive) { lan.turnOff() }
                }
            } else {
                LabeledContent("Office code") {
                    HStack {
                        Group {
                            if showCode { TextField("", text: $code, prompt: Text("same on every computer")) }
                            else { SecureField("", text: $code, prompt: Text("same on every computer")) }
                        }
                        .frame(maxWidth: 260)
                        Button { showCode.toggle() } label: { Image(systemName: showCode ? "eye.slash" : "eye") }
                            .buttonStyle(.borderless)
                    }
                }
                LabeledContent("") {
                    Button { lan.turnOn(code: code); code = "" } label: {
                        Label("Turn On Office Sync", systemImage: "network")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(code.trimmed.count < 4)
                }
            }
        } header: {
            Label("Office Network Sync (free – no internet needed)", systemImage: "network")
        } footer: {
            VStack(alignment: .leading, spacing: 4) {
                Text("Computers on the same Wi-Fi or office network find each other and keep the same data – instantly, without internet and without any monthly cost.")
                Text("Turn it on on every computer with the same office code (at least 4 characters). The first time, macOS asks to allow “AFAN Traders” to find devices on your network – click Allow.")
                Text("You can use this together with Cloud Sync: office computers sync directly, and one connected computer keeps the cloud up to date for anyone outside the office.")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .multilineTextAlignment(.leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var icon: String {
        guard lan.enabled else { return "network.slash" }
        return lan.peers.values.contains(where: \.connected) ? "checkmark.circle.fill" : "antenna.radiowaves.left.and.right"
    }

    private var color: Color {
        guard lan.enabled else { return .secondary }
        if !lan.problems.isEmpty { return Theme.orange }
        return lan.peers.values.contains(where: \.connected) ? Theme.green : .secondary
    }
}

/// Sidebar line for office sync (only when it is on).
struct SidebarOfficeStatus: View {
    @ObservedObject var lan: LANSync
    var open: () -> Void

    var body: some View {
        if lan.enabled {
            Button(action: open) {
                HStack(spacing: 8) {
                    Image(systemName: "network").foregroundStyle(connected > 0 ? Theme.green : .secondary)
                    Text(connected > 0 ? "Office: \(connected) computer\(connected == 1 ? "" : "s") connected" : "Office: looking for computers…")
                        .lineLimit(1)
                    Spacer()
                }
                .font(.caption.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
    }

    private var connected: Int { lan.peers.values.filter(\.connected).count }
}

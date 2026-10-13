import SwiftUI
import AppKit

/// Settings → Cloud Sync.
struct CloudSyncSection: View {
    @ObservedObject var sync: SyncEngine
    @State private var url = ""
    @State private var anonKey = ""
    @State private var email = ""
    @State private var password = ""
    @State private var busy = false
    @State private var error: String?
    @State private var copied = false

    var body: some View {
        Section {
            LabeledContent("Status") {
                HStack(spacing: 8) {
                    SyncDot(sync: sync)
                    Text(sync.statusText).foregroundStyle(statusColor)
                }
            }

            if sync.config.enabled {
                LabeledContent("Project") { Text(sync.config.url).foregroundStyle(.secondary).textSelection(.enabled) }
                LabeledContent("Signed in as") { Text(sync.config.email) }
                LabeledContent("Realtime") {
                    Label(sync.live ? "Connected – changes from other computers appear instantly" : "Not connected (will retry)",
                          systemImage: sync.live ? "bolt.horizontal.fill" : "bolt.horizontal")
                        .foregroundStyle(sync.live ? Theme.green : .secondary)
                }
                if !sync.problems.isEmpty {
                    LabeledContent("Needs attention") {
                        VStack(alignment: .leading, spacing: 2) {
                            ForEach(sync.problems.prefix(5), id: \.self) { Text($0).font(.caption) }
                        }
                        .foregroundStyle(Theme.orange)
                    }
                }
                LabeledContent("") {
                    HStack {
                        Button { sync.syncNow() } label: { Label("Sync Now", systemImage: "arrow.triangle.2.circlepath") }
                            .disabled(sync.phase == .syncing)
                        Button("Disconnect", role: .destructive) { sync.disconnect() }
                    }
                }
            } else {
                TextField("Project URL", text: $url, prompt: Text("https://xxxx.supabase.co"))
                TextField("Anon public key", text: $anonKey, prompt: Text("eyJhbGciOi…"))
                TextField("Email", text: $email, prompt: Text("Supabase user email"))
                SecureField("Password", text: $password)
                if let error {
                    Text(error).foregroundStyle(Theme.red).font(.callout)
                }
                LabeledContent("") {
                    HStack {
                        Button {
                            connect()
                        } label: {
                            if busy { ProgressView().controlSize(.small) } else { Label("Connect & Sign In", systemImage: "icloud.and.arrow.up") }
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(busy || url.trimmed.isEmpty || anonKey.trimmed.isEmpty || email.trimmed.isEmpty || password.isEmpty)
                    }
                }
            }

            LabeledContent("Setup") {
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(SupabaseSetup.sql, forType: .string)
                    copied = true
                } label: {
                    Label(copied ? "Copied – paste it in Supabase SQL Editor" : "Copy Setup SQL",
                          systemImage: copied ? "checkmark" : "doc.on.doc")
                }
            }
        } header: {
            Label("Cloud Sync (Supabase)", systemImage: "icloud")
        } footer: {
            VStack(alignment: .leading, spacing: 4) {
                Text("How to set up (one time):").fontWeight(.semibold)
                Text("1. Create a free project at supabase.com.")
                Text("2. Click “Copy Setup SQL”, open SQL Editor in Supabase, paste and Run.")
                Text("3. In Supabase → Authentication → Users, add a user (email + password) for your office.")
                Text("4. In Supabase → Project Settings → API, copy the Project URL and the anon public key into the fields above.")
                Text("The app keeps working without internet. Changes are saved on this Mac first and uploaded automatically when you are back online.")
                    .padding(.top, 2)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .onAppear {
            url = sync.config.url
            anonKey = sync.config.anonKey
            email = sync.config.email
        }
    }

    private var statusColor: Color {
        switch sync.phase {
        case .error: return Theme.red
        case .offline: return Theme.orange
        case .idle: return sync.pending > 0 ? Theme.orange : Theme.green
        default: return .secondary
        }
    }

    private func connect() {
        busy = true
        error = nil
        Task {
            do {
                try await sync.connect(url: url, anonKey: anonKey, email: email, password: password)
                password = ""
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

/// Small coloured status dot used in Settings and the sidebar.
struct SyncDot: View {
    @ObservedObject var sync: SyncEngine

    var body: some View {
        Image(systemName: icon)
            .foregroundStyle(color)
            .symbolEffect(.pulse, isActive: sync.phase == .syncing || sync.phase == .connecting)
    }

    private var icon: String {
        switch sync.phase {
        case .off: return "icloud.slash"
        case .offline: return "wifi.slash"
        case .error: return "exclamationmark.icloud"
        case .syncing, .connecting: return "arrow.triangle.2.circlepath.icloud"
        case .idle: return sync.pending > 0 ? "icloud.and.arrow.up" : "checkmark.icloud"
        }
    }

    private var color: Color {
        switch sync.phase {
        case .off: return .secondary
        case .offline: return Theme.orange
        case .error: return Theme.red
        case .syncing, .connecting: return Theme.blue
        case .idle: return sync.pending > 0 ? Theme.orange : Theme.green
        }
    }
}

/// Sidebar footer line showing sync status; click to open Settings.
struct SidebarSyncStatus: View {
    @ObservedObject var sync: SyncEngine
    var open: () -> Void

    var body: some View {
        Button(action: open) {
            HStack(spacing: 8) {
                SyncDot(sync: sync)
                Text(sync.config.enabled ? sync.statusText : "Cloud sync off")
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer()
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Cloud sync status – click for settings")
    }
}

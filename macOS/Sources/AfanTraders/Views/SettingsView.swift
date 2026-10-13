import SwiftUI
import UniformTypeIdentifiers

struct SettingsView: View {
    @EnvironmentObject var store: AppStore
    @State private var s = CompanySettings()
    @State private var loaded = false
    @State private var preview: PreviewDoc?
    @State private var message: String?
    @State private var saveTask: Task<Void, Never>?

    private var dirty: Bool { loaded && s != store.settings }

    var body: some View {
        Form {
            Section {
                TextField("Company name", text: $s.companyName)
                TextField("Tagline", text: $s.tagline)
                TextField("Address", text: $s.address, axis: .vertical).lineLimit(2...4)
                TextField("Phone", text: $s.phone)
                TextField("Email", text: $s.email)
                TextField("NTN", text: $s.ntn)
                TextField("STRN (Sales Tax Reg. No.)", text: $s.strn)
                LabeledContent("Logo") {
                    HStack {
                        ItemThumb(data: s.logo, size: 44)
                        Button(s.logo == nil ? "Choose…" : "Change…") {
                            if let d = ImageUtil.pickImage(maxSide: 800, keepTransparency: true) { s.logo = d }
                        }
                        if s.logo != nil { Button("Remove") { s.logo = nil } }
                    }
                }
            } header: {
                Label("Company", systemImage: "building.2")
            } footer: {
                Text("Used on printed documents when no letterhead is set.").foregroundStyle(.secondary)
            }

            Section {
                LabeledContent("Letterhead") {
                    HStack {
                        if s.letterhead != nil {
                            Label("Letterhead added", systemImage: "checkmark.circle.fill").foregroundStyle(Theme.green)
                        } else {
                            Text("Not set – the built-in header is used").foregroundStyle(.secondary)
                        }
                        Button(s.letterhead == nil ? "Choose PDF or Image…" : "Change…") { pickLetterhead() }
                        if s.letterhead != nil { Button("Remove") { s.letterhead = nil } }
                    }
                }
                if s.letterhead != nil {
                    LabeledContent("Space for header (top)") {
                        HStack {
                            Slider(value: $s.letterheadTop, in: 20...320, step: 5)
                            Text("\(Int(s.letterheadTop)) pt").monospacedDigit().frame(width: 56)
                        }
                    }
                    LabeledContent("Space for footer (bottom)") {
                        HStack {
                            Slider(value: $s.letterheadBottom, in: 10...240, step: 5)
                            Text("\(Int(s.letterheadBottom)) pt").monospacedDigit().frame(width: 56)
                        }
                    }
                }
                LabeledContent("") {
                    Button { preview = samplePreview() } label: { Label("Preview Sample Invoice", systemImage: "eye") }
                }
            } header: {
                Label("Letterhead", systemImage: "doc.richtext")
            } footer: {
                Text("Pick your letterhead PDF. Quotations, delivery challans and invoices will print on it. Adjust the spaces so text doesn't overlap your header and footer.")
                    .foregroundStyle(.secondary)
            }

            Section {
                LabeledContent("Default sales tax") {
                    HStack {
                        NumField(value: $s.defaultTaxRate, width: 80)
                        Text("%")
                    }
                }
                TextField("Currency symbol", text: $s.currency)
                TextField("Currency in words", text: $s.currencyWord)
            } header: {
                Label("Tax & Currency", systemImage: "percent")
            } footer: {
                Text("New quotations start with this tax rate. You can still change it on each quotation and invoice.")
                    .foregroundStyle(.secondary)
            }

            Section {
                TextField("Quotation prefix", text: $s.quotationPrefix)
                TextField("Delivery challan prefix", text: $s.challanPrefix)
                TextField("Invoice prefix", text: $s.invoicePrefix)
                TextField("Invoice title", text: $s.invoiceTitle)
            } header: {
                Label("Document Numbers", systemImage: "number")
            } footer: {
                Text("Numbers are generated like \(s.quotationPrefix.isEmpty ? "" : s.quotationPrefix + "-")\(String(Calendar.current.component(.year, from: Date())).suffix(2))-48213 and can be changed on every document.")
                    .foregroundStyle(.secondary)
            }

            Section {
                TextField("Quotation terms", text: $s.quotationTerms, axis: .vertical).lineLimit(3...6)
                TextField("Invoice terms", text: $s.invoiceTerms, axis: .vertical).lineLimit(3...6)
            } header: {
                Label("Terms & Conditions", systemImage: "text.alignleft")
            }

            OfficeSyncSection(lan: store.lan)

            CloudSyncSection(sync: store.sync)

            Section {
                LabeledContent("Data folder") {
                    HStack {
                        Text(store.dataFolder.path).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([store.dataFolder]) }
                    }
                }
                LabeledContent("Backup") {
                    Button { backup() } label: { Label("Save a Backup Copy…", systemImage: "externaldrive.badge.checkmark") }
                }
            } header: {
                Label("Data & Backup", systemImage: "externaldrive")
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Settings")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Label(dirty ? "Saving…" : "All changes saved", systemImage: dirty ? "arrow.triangle.2.circlepath" : "checkmark.circle.fill")
                    .labelStyle(.titleAndIcon)
                    .foregroundStyle(dirty ? Color.secondary : Theme.green)
                    .help("Settings save automatically")
            }
        }
        .onAppear {
            if !loaded { s = store.settings; loaded = true }
        }
        // Settings save automatically: pictures at once, typing shortly after you stop.
        .onChange(of: s) { old, new in
            guard loaded, new != store.settings else { return }
            saveTask?.cancel()
            let immediate = old.letterhead != new.letterhead || old.logo != new.logo
            saveTask = Task { @MainActor in
                if !immediate { try? await Task.sleep(nanoseconds: 700_000_000) }
                guard !Task.isCancelled else { return }
                save()
            }
        }
        .onDisappear { if dirty { save() } }
        .sheet(item: $preview) { PDFPreviewSheet(doc: $0) }
        .alert(message ?? "", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
            Button("OK", role: .cancel) {}
        }
    }

    private func save() {
        do {
            try store.saveSettings(s)
        } catch { message = "Could not save settings: \(error.localizedDescription)" }
    }

    private func pickLetterhead() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url, let data = try? Data(contentsOf: url) else { return }
        if Letterhead(data: data) == nil {
            message = "That file could not be read as a letterhead."
        } else {
            s.letterhead = data
        }
    }

    private func backup() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "sqlite") ?? .data]
        panel.nameFieldStringValue = "AFAN Traders Backup \(Fmt.date(Date())).sqlite"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try store.backup(to: url)
            message = "Backup saved."
        } catch { message = error.localizedDescription }
    }

    private func samplePreview() -> PreviewDoc {
        store.preview(.sample(.invoice, settings: s), settings: s)
    }

}

import SwiftUI
import AppKit

@main
struct AfanTradersApp: App {
    @StateObject private var store = AppStore(folder: AfanTradersApp.dataFolderArgument)

    /// `--data <folder>` opens a different database (handy for training or testing).
    static var dataFolderArgument: URL? {
        guard let i = CommandLine.arguments.firstIndex(of: "--data"), i + 1 < CommandLine.arguments.count else { return nil }
        return URL(fileURLWithPath: CommandLine.arguments[i + 1])
    }

    init() {
        if let i = CommandLine.arguments.firstIndex(of: "--selftest"), i + 1 < CommandLine.arguments.count {
            SelfTest.run(folder: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
        }
        if let i = CommandLine.arguments.firstIndex(of: "--synctest"), i + 1 < CommandLine.arguments.count {
            SyncTest.run(folder: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
        }
        // --letterhead-test <letterhead.pdf|jpg> <out.pdf>: prints a sample quotation on that letterhead.
        if let i = CommandLine.arguments.firstIndex(of: "--letterhead-test"), i + 2 < CommandLine.arguments.count {
            var s = CompanySettings()
            s.letterhead = try? Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
            print(Letterhead(data: s.letterhead) == nil ? "LETTERHEAD NOT READABLE" : "letterhead ok")
            let pdf = MainActor.assumeIsolated { PDFRenderer.render(.sample(.quotation, settings: s), settings: s) }
            try? pdf.write(to: URL(fileURLWithPath: CommandLine.arguments[i + 2]))
            exit(0)
        }
        if let i = CommandLine.arguments.firstIndex(of: "--lantest"), i + 1 < CommandLine.arguments.count {
            LANTest.run(folder: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
        }
        NSApplication.shared.setActivationPolicy(.regular)
    }

    var body: some Scene {
        WindowGroup("AFAN Traders") {
            RootView()
                .environmentObject(store)
                .frame(minWidth: 1120, minHeight: 720)
                .onAppear { NSApp.activate(ignoringOtherApps: true) }
        }
        .defaultSize(width: 1360, height: 860)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New Quotation") { store.open(.quotation(store.draftQuotation())) }
                    .keyboardShortcut("n", modifiers: .command)
                Button("Add Items / Stock…") { store.showCost = true; store.open(.purchase(Purchase())) }
                    .keyboardShortcut("n", modifiers: [.command, .shift])
                Button("New Single Item") { store.open(.item(Item())) }
                    .keyboardShortcut("i", modifiers: [.command, .shift])
            }
            CommandGroup(after: .sidebar) {
                Button(store.showCost ? "Hide Cost Prices" : "Show Cost Prices") { store.showCost.toggle() }
                    .keyboardShortcut("k", modifiers: [.command, .shift])
                Divider()
            }
        }
    }
}

import SwiftUI

/// Type an Item ID or name; matching inventory items drop down. Click one
/// (or press Return) to use its saved details.
struct ItemSearchField: View {
    @EnvironmentObject var store: AppStore
    var placeholder = "Type Item ID or name to add an item…"
    /// When true, pressing Return on an unknown ID offers to add it as a new item.
    var allowNew = false
    var onPick: (Item) -> Void
    var onNew: (String) -> Void = { _ in }

    @State private var text = ""
    @State private var showList = false
    @State private var highlight = 0
    @State private var navigated = false

    private var query: String { text.trimmed.lowercased() }

    private var matches: [Item] {
        guard !query.isEmpty else { return [] }
        let starts = store.items.filter { $0.code.lowercased().hasPrefix(query) }
        let others = store.items.filter {
            !$0.code.lowercased().hasPrefix(query) &&
            ($0.code.lowercased().contains(query) || $0.name.lowercased().contains(query) ||
             $0.details.lowercased().contains(query))
        }
        return Array((starts + others).prefix(8))
    }

    private var exactMatch: Bool { store.item(code: text) != nil }

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField(placeholder, text: $text)
                .textFieldStyle(.plain)
                .font(.body)
                .onChange(of: text) { _, _ in
                    showList = true
                    highlight = 0
                    navigated = false
                }
                .onSubmit(submit)
                .onKeyPress(.downArrow) { move(1); return .handled }
                .onKeyPress(.upArrow) { move(-1); return .handled }
                .onKeyPress(.escape) { showList = false; return .handled }
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.accentColor.opacity(0.45), lineWidth: 1))
        .overlay(alignment: .topLeading) {
            if showList && !query.isEmpty && (!matches.isEmpty || allowNew) {
                suggestions.offset(y: 42)
            }
        }
    }

    private var suggestions: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(matches.enumerated()), id: \.element.id) { idx, item in
                HStack(spacing: 10) {
                    ItemThumb(data: item.imageData, size: 30)
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 6) {
                            Text(item.code).font(.callout.weight(.semibold))
                            Text(item.name).font(.callout).lineLimit(1)
                        }
                        if !item.details.isEmpty {
                            Text(item.details).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    Spacer(minLength: 12)
                    VStack(alignment: .trailing, spacing: 1) {
                        Text(Fmt.money(item.salePrice, store.settings.currency)).font(.callout.monospacedDigit())
                        Text("Stock: \(Fmt.qty(item.stock)) \(item.unit)")
                            .font(.caption)
                            .foregroundStyle(item.stock > 0 ? Color.secondary : Theme.red)
                    }
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(idx == highlight ? Color.accentColor.opacity(0.15) : Color.clear)
                .contentShape(Rectangle())
                .onTapGesture { pick(item) }
                .onHover { if $0 { highlight = idx } }
            }
            if allowNew && !exactMatch {
                if !matches.isEmpty { Divider() }
                HStack(spacing: 8) {
                    Image(systemName: "plus.circle.fill").foregroundStyle(Theme.green)
                    Text("Add new item with ID “\(text.trimmed)”").font(.callout)
                    Spacer()
                    Text("Return ↩").font(.caption).foregroundStyle(.secondary)
                }
                .padding(10)
                .contentShape(Rectangle())
                .onTapGesture { createNew() }
            }
        }
        .padding(.vertical, 4)
        .frame(width: 560, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .windowBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.18), radius: 12, y: 6)
    }

    private func move(_ delta: Int) {
        guard !matches.isEmpty else { return }
        showList = true
        navigated = true
        highlight = max(0, min(matches.count - 1, highlight + delta))
    }

    private func submit() {
        if let exact = store.item(code: text) {
            pick(exact)
        } else if !matches.isEmpty, !allowNew || navigated {
            pick(matches[min(highlight, matches.count - 1)])
        } else if allowNew, !query.isEmpty {
            createNew()
        }
    }

    private func pick(_ item: Item) {
        onPick(item)
        text = ""
        showList = false
    }

    private func createNew() {
        onNew(text.trimmed)
        text = ""
        showList = false
    }
}

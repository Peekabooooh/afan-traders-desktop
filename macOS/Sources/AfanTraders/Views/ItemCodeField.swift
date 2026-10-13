import SwiftUI

/// Item ID box for one row of the bulk adder. Suggests existing items while
/// typing; picking one fills the row with that item's saved details.
struct ItemCodeField: View {
    @EnvironmentObject var store: AppStore
    @Binding var code: String
    /// True while the suggestion list is open (the row raises itself above the rows below).
    @Binding var active: Bool
    var onPick: (Item) -> Void

    @State private var showList = false
    @State private var highlight = 0
    @FocusState private var focused: Bool

    private var query: String { code.trimmed.lowercased() }

    private var matches: [Item] {
        guard !query.isEmpty, store.item(code: code) == nil else { return [] }
        let starts = store.items.filter { $0.code.lowercased().hasPrefix(query) }
        let others = store.items.filter {
            !$0.code.lowercased().hasPrefix(query) &&
            ($0.code.lowercased().contains(query) || $0.name.lowercased().contains(query))
        }
        return Array((starts + others).prefix(6))
    }

    private var open: Bool { showList && !matches.isEmpty }

    var body: some View {
        TextField("Item ID", text: $code)
            .textFieldStyle(.roundedBorder)
            .focused($focused)
            .onChange(of: code) { _, _ in
                if focused { showList = true; highlight = 0 }
            }
            .onChange(of: open) { _, isOpen in active = isOpen }
            .onChange(of: focused) { _, isFocused in
                // Small delay so a click on a suggestion still lands.
                if !isFocused { DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { showList = false } }
            }
            .onKeyPress(.downArrow) {
                guard open else { return .ignored }
                highlight = min(matches.count - 1, highlight + 1); return .handled
            }
            .onKeyPress(.upArrow) {
                guard open else { return .ignored }
                highlight = max(0, highlight - 1); return .handled
            }
            .onKeyPress(.escape) { showList = false; return .handled }
            .onSubmit {
                if open { pick(matches[min(highlight, matches.count - 1)]) }
            }
            .overlay(alignment: .topLeading) {
                if open { list.offset(y: 28) }
            }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Existing items – click to use saved details")
                .font(.caption2).foregroundStyle(.secondary)
                .padding(.horizontal, 10).padding(.top, 6).padding(.bottom, 2)
            ForEach(Array(matches.enumerated()), id: \.element.id) { idx, item in
                HStack(spacing: 8) {
                    ItemThumb(data: item.imageData, size: 26)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(item.code).font(.callout.weight(.semibold))
                        Text(item.name).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 10)
                    Text("Stock \(Fmt.qty(item.stock))").font(.caption).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(idx == highlight ? Color.accentColor.opacity(0.15) : Color.clear)
                .contentShape(Rectangle())
                .onTapGesture { pick(item) }
                .onHover { if $0 { highlight = idx } }
            }
        }
        .padding(.bottom, 4)
        .frame(width: 340, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .windowBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.18), radius: 10, y: 5)
    }

    private func pick(_ item: Item) {
        onPick(item)
        showList = false
    }
}

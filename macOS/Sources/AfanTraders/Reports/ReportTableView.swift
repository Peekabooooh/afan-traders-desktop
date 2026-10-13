import SwiftUI

/// Sortable, searchable table for any report. Click a header to sort;
/// double-click a row to open its document.
struct ReportTableView: View {
    @EnvironmentObject var store: AppStore
    let table: ReportTable
    let rows: [ReportRow]
    @Binding var sortColumn: Int?
    @Binding var ascending: Bool
    @State private var selected: UUID?

    private let rowHeight: CGFloat = 26

    var body: some View {
        if rows.isEmpty {
            ContentUnavailableView("Nothing in this period", systemImage: "tray",
                                   description: Text("Try a wider date range, such as This Year or All Time."))
        } else {
            ScrollView([.vertical, .horizontal]) {
                LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                    Section {
                        ForEach(Array(rows.enumerated()), id: \.element.id) { idx, row in
                            line(row.cells, bold: false)
                                .frame(height: rowHeight)
                                .background(rowBackground(idx, row))
                                .contentShape(Rectangle())
                                .onTapGesture(count: 2) { if let k = row.open { store.open(k) } }
                                .simultaneousGesture(TapGesture().onEnded { selected = row.id })
                                .help(row.open == nil ? "" : "Double-click to open")
                        }
                    } header: {
                        VStack(spacing: 0) {
                            header
                            if let totals = table.totals(for: rows) {
                                line(totals, bold: true)
                                    .frame(height: rowHeight + 2)
                                    .background(Theme.navy.opacity(0.08))
                            }
                            Divider()
                        }
                        .background(Color(nsColor: .windowBackgroundColor))
                    }
                }
                .frame(minWidth: totalWidth, alignment: .leading)
            }
        }
    }

    private var totalWidth: CGFloat { table.columns.reduce(24) { $0 + $1.width } }

    private func rowBackground(_ idx: Int, _ row: ReportRow) -> Color {
        if row.id == selected { return Color.accentColor.opacity(0.18) }
        return idx.isMultiple(of: 2) ? .clear : Color.primary.opacity(0.035)
    }

    private var header: some View {
        HStack(spacing: 0) {
            ForEach(Array(table.columns.enumerated()), id: \.offset) { i, col in
                Button {
                    if sortColumn == i { ascending.toggle() } else { sortColumn = i; ascending = !col.numeric }
                } label: {
                    HStack(spacing: 3) {
                        if col.numeric { Spacer(minLength: 0) }
                        if col.sensitive { Image(systemName: store.showCost ? "eye" : "eye.slash").font(.system(size: 8)) }
                        Text(col.title).lineLimit(1)
                        if sortColumn == i {
                            Image(systemName: ascending ? "chevron.up" : "chevron.down").font(.system(size: 8, weight: .bold))
                        }
                        if !col.numeric { Spacer(minLength: 0) }
                    }
                    .padding(.horizontal, 8)
                    .frame(width: col.width, height: 30)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
    }

    private func line(_ cells: [ReportCell], bold: Bool) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(table.columns.enumerated()), id: \.offset) { i, col in
                let masked = col.sensitive && !store.showCost && !cells[i].text.isEmpty
                Text(masked ? "••••" : cells[i].text)
                    .font(.system(size: 12, weight: bold ? .semibold : .regular))
                    .monospacedDigit()
                    .foregroundStyle(masked ? Color.secondary.opacity(0.5) : profitColor(col, cells[i]))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .padding(.horizontal, 8)
                    .frame(width: col.width, alignment: col.numeric ? .trailing : .leading)
            }
        }
        .padding(.horizontal, 12)
    }

    private func profitColor(_ col: ReportColumn, _ cell: ReportCell) -> Color {
        if col.sensitive, let v = cell.value, v < 0, col.kind == .money || col.kind == .percent { return Theme.red }
        return .primary
    }
}

extension ReportTable {
    func filteredSorted(search: String, sort: Int?, ascending: Bool) -> [ReportRow] {
        let q = search.trimmed.lowercased()
        var out = q.isEmpty ? rows : rows.filter { $0.cells.contains { $0.text.lowercased().contains(q) } }
        if let s = sort, s < columns.count {
            out.sort { a, b in
                let x = a.cells[s], y = b.cells[s]
                let less: Bool
                if let xv = x.value, let yv = y.value { less = xv < yv }
                else if x.value != nil || y.value != nil { less = x.value == nil }
                else { less = x.text.localizedCaseInsensitiveCompare(y.text) == .orderedAscending }
                return ascending ? less : !less
            }
        }
        return out
    }
}

import SwiftUI

/// Line items table shared by quotations, delivery challans and invoices.
struct DocLinesEditor: View {
    enum Mode { case quotation, challan, invoice }

    @EnvironmentObject var store: AppStore
    @Binding var lines: [DocLine]
    let mode: Mode
    var locked = false

    private var showPrices: Bool { mode != .challan }
    private var showCostColumns: Bool { showPrices && store.showCost }
    private var canEditQty: Bool { mode == .quotation && !locked }
    private var canEditRate: Bool { mode != .challan && !locked }
    private var canDelete: Bool { mode == .quotation && !locked }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if lines.isEmpty {
                VStack(spacing: 6) {
                    Image(systemName: "tray").font(.title2).foregroundStyle(.tertiary)
                    Text(mode == .quotation ? "No items yet. Search above to add items from inventory."
                                            : "No items.")
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 28)
            }
            ForEach(Array(lines.enumerated()), id: \.element.id) { idx, line in
                row(idx: idx, line: line)
                    .background(idx.isMultiple(of: 2) ? Color.clear : Color.primary.opacity(0.03))
                if idx < lines.count - 1 { Divider().opacity(0.5) }
            }
        }
        .card(padding: 0)
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text("#").frame(width: 26, alignment: .leading)
            Text("Item ID").frame(width: 110, alignment: .leading)
            Text("Item / Description").frame(maxWidth: .infinity, alignment: .leading)
            Text("Unit").frame(width: 50, alignment: .leading)
            Text("Qty").frame(width: 80, alignment: .trailing)
            if showPrices {
                Text(mode == .invoice ? "Invoice Rate" : "Rate").frame(width: 110, alignment: .trailing)
                Text("Amount").frame(width: 120, alignment: .trailing)
            }
            if showCostColumns {
                Text("Cost").frame(width: 90, alignment: .trailing)
                Text("Profit").frame(width: 100, alignment: .trailing)
            }
            if canDelete { Color.clear.frame(width: 24) }
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private func binding(_ idx: Int) -> Binding<DocLine> {
        Binding(
            get: { idx < lines.count ? lines[idx] : DocLine() },
            set: { if idx < lines.count { lines[idx] = $0 } }
        )
    }

    @ViewBuilder
    private func row(idx: Int, line: DocLine) -> some View {
        let item = store.item(id: line.itemId)
        HStack(alignment: .center, spacing: 8) {
            Text("\(idx + 1)").foregroundStyle(.secondary).frame(width: 26, alignment: .leading)
            HStack(spacing: 6) {
                ItemThumb(data: item?.imageData, size: 24)
                Text(line.code).fontWeight(.semibold).lineLimit(1)
            }
            .frame(width: 110, alignment: .leading)

            VStack(alignment: .leading, spacing: 3) {
                if mode == .quotation && !locked {
                    TextField("Item name", text: binding(idx).name).textFieldStyle(.roundedBorder)
                    TextField("Description (optional)", text: binding(idx).details)
                        .textFieldStyle(.roundedBorder).font(.caption)
                } else {
                    Text(line.name).lineLimit(1)
                    if !line.details.isEmpty {
                        Text(line.details).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
                if mode == .quotation, let item, item.stock < line.qty {
                    Label("Only \(Fmt.qty(item.stock)) in stock", systemImage: "exclamationmark.triangle.fill")
                        .font(.caption2).foregroundStyle(Theme.orange)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Text(line.unit).foregroundStyle(.secondary).frame(width: 50, alignment: .leading)

            Group {
                if canEditQty {
                    NumField(value: binding(idx).qty)
                } else {
                    Text(Fmt.qty(line.qty)).monospacedDigit()
                }
            }
            .frame(width: 80, alignment: .trailing)

            if showPrices {
                VStack(alignment: .trailing, spacing: 2) {
                    if canEditRate {
                        NumField(value: binding(idx).rate)
                    } else {
                        Text(Fmt.money(line.rate)).monospacedDigit()
                    }
                    if mode == .invoice && line.baseRate != line.rate {
                        Text("Quoted: \(Fmt.money(line.baseRate))").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                .frame(width: 110, alignment: .trailing)
                Text(Fmt.money(line.total))
                    .fontWeight(.medium).monospacedDigit()
                    .frame(width: 120, alignment: .trailing)
            }
            if showCostColumns {
                let cost = item?.costPrice ?? 0
                let profit = ((line.rate - cost) * line.qty).rounded2
                Text(item == nil ? "—" : Fmt.money(cost)).foregroundStyle(.secondary).monospacedDigit()
                    .frame(width: 90, alignment: .trailing)
                Text(item == nil ? "—" : Fmt.money(profit))
                    .foregroundStyle(profit < 0 ? Theme.red : Theme.green).monospacedDigit()
                    .frame(width: 100, alignment: .trailing)
            }
            if canDelete {
                Button {
                    lines.removeAll { $0.id == line.id }
                } label: {
                    Image(systemName: "trash").foregroundStyle(Theme.red)
                }
                .buttonStyle(.plain)
                .help("Remove this item")
                .frame(width: 24)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}

/// Subtotal / sales tax / grand total box.
struct TotalsBox: View {
    @EnvironmentObject var store: AppStore
    let subtotal: Double
    @Binding var taxRate: Double
    var editableTax = true
    var profit: Double? = nil

    private var tax: Double { (subtotal * taxRate / 100).rounded2 }
    private var currency: String { store.settings.currency }

    var body: some View {
        VStack(spacing: 10) {
            row("Subtotal", Fmt.money(subtotal, currency))
            HStack {
                Text("Sales Tax")
                if editableTax {
                    NumField(value: $taxRate, width: 60)
                    Text("%")
                } else {
                    Text("(\(Fmt.qty(taxRate))%)").foregroundStyle(.secondary)
                }
                Spacer()
                Text(Fmt.money(tax, currency)).monospacedDigit()
            }
            Divider()
            HStack {
                Text("Grand Total").font(.headline)
                Spacer()
                Text(Fmt.money(subtotal + tax, currency)).font(.title3.weight(.bold)).monospacedDigit()
                    .foregroundStyle(Theme.navy)
            }
            if let profit, store.showCost {
                HStack {
                    Label("Profit (before tax)", systemImage: "chart.line.uptrend.xyaxis").font(.caption)
                    Spacer()
                    Text(Fmt.money(profit, currency)).font(.caption.weight(.semibold)).monospacedDigit()
                        .foregroundStyle(profit < 0 ? Theme.red : Theme.green)
                }
            }
        }
        .frame(width: 330)
        .card()
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack { Text(label); Spacer(); Text(value).monospacedDigit() }
    }
}

extension AppStore {
    func profit(of lines: [DocLine]) -> Double {
        lines.reduce(0) { sum, l in
            guard let it = item(id: l.itemId) else { return sum }
            return sum + (l.rate - it.costPrice) * l.qty
        }.rounded2
    }
}

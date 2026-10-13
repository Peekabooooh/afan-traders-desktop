import SwiftUI

extension OrderStage {
    var color: Color {
        switch self {
        case .needsChallan: return Theme.blue
        case .awaitingApproval: return Theme.orange
        case .readyToInvoice: return Theme.green
        case .invoiced: return .secondary
        }
    }
    var icon: String {
        switch self {
        case .needsChallan: return "truck.box"
        case .awaitingApproval: return "clock"
        case .readyToInvoice: return "banknote"
        case .invoiced: return "checkmark.circle"
        }
    }
    var shortTitle: String {
        switch self {
        case .needsChallan: return "Needs DC"
        case .awaitingApproval: return "DC Pending"
        case .readyToInvoice: return "Ready to Invoice"
        case .invoiced: return "Invoiced"
        }
    }
}

struct QuotationsView: View {
    @EnvironmentObject var store: AppStore
    @State private var search = ""

    private var rows: [Quotation] {
        let q = search.trimmed.lowercased()
        return store.quotations.filter { quote in
            if let f = store.quotationFilter, store.stage(of: quote) != f { return false }
            guard !q.isEmpty else { return true }
            return quote.number.lowercased().contains(q) || quote.poNumber.lowercased().contains(q)
                || quote.customerName.lowercased().contains(q)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Picker("Show", selection: $store.quotationFilter) {
                    Text("All").tag(OrderStage?.none)
                    ForEach(OrderStage.allCases) { stage in
                        Text(stage.shortTitle).tag(OrderStage?.some(stage))
                    }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 560)
                Spacer()
                Text("\(rows.count) quotation\(rows.count == 1 ? "" : "s")").foregroundStyle(.secondary)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            Divider()
            if store.quotations.isEmpty {
                ContentUnavailableView {
                    Label("No quotations yet", systemImage: "doc.text")
                } description: {
                    Text("Create your first quotation from items in your inventory.")
                } actions: {
                    Button("New Quotation") { store.open(.quotation(store.draftQuotation())) }
                        .buttonStyle(.borderedProminent)
                }
            } else {
                QuotationTable(quotes: rows)
            }
        }
        .navigationTitle("Quotations")
        .searchable(text: $search, prompt: "Search quotation No., PO No. or customer")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { store.open(.quotation(store.draftQuotation())) } label: {
                    Label("New Quotation", systemImage: "plus")
                }
                .labelStyle(.titleAndIcon)
                .keyboardShortcut("n", modifiers: .command)
            }
        }
    }
}

/// The order tracker: every quotation with its delivery challan and invoice status.
struct QuotationTable: View {
    @EnvironmentObject var store: AppStore
    let quotes: [Quotation]
    @State private var selection: Quotation.ID?

    var body: some View {
        Table(quotes, selection: $selection) {
            TableColumn("Quotation No.") { (q: Quotation) in
                Text(q.number).fontWeight(.semibold)
            }
            .width(min: 110, ideal: 120)
            TableColumn("Date") { (q: Quotation) in Text(Fmt.date(q.date)) }
                .width(min: 85, ideal: 95)
            TableColumn("PO No.") { (q: Quotation) in
                Text(q.poNumber.isEmpty ? "—" : q.poNumber).foregroundStyle(q.poNumber.isEmpty ? .secondary : .primary)
            }
            .width(min: 80, ideal: 110)
            TableColumn("Customer") { (q: Quotation) in Text(q.customerName).lineLimit(1) }
                .width(min: 140, ideal: 190)
            TableColumn("Total") { (q: Quotation) in
                Text(Fmt.money(q.grandTotal)).monospacedDigit().frame(maxWidth: .infinity, alignment: .trailing)
            }
            .width(min: 90, ideal: 105)
            TableColumn("Delivery Challan") { (q: Quotation) in challanCell(q) }
                .width(min: 130, ideal: 150)
            TableColumn("Invoice") { (q: Quotation) in invoiceCell(q) }
                .width(min: 100, ideal: 120)
            TableColumn("Next Step") { (q: Quotation) in NextStepButton(store: store, quote: q) }
                .width(min: 130, ideal: 150)
        }
        .contextMenu(forSelectionType: Quotation.ID.self) { ids in
            if let id = ids.first, let q = store.quotation(id: id) {
                Button("Open Quotation") { store.open(.quotation(q)) }
                Button("Preview / Print Quotation") { store.open(.preview(store.preview(store.printable(q)))) }
                if let c = store.challan(forQuotation: q.id) {
                    Button("Open Delivery Challan \(c.number)") { store.open(.challan(c)) }
                } else {
                    Button("Create Delivery Challan") { store.open(.challan(store.draftChallan(from: q))) }
                }
                if let inv = store.invoice(forQuotation: q.id) {
                    Button("Open Invoice \(inv.number)") { store.open(.invoice(inv)) }
                }
                Divider()
                Button("Duplicate as New Quotation") {
                    var copy = q
                    copy.id = 0
                    copy.number = store.newQuotationNumber()
                    copy.date = Date()
                    copy.poNumber = ""
                    copy.lines = q.lines.map { var l = $0; l.id = UUID(); return l }
                    store.open(.quotation(copy))
                }
            }
        } primaryAction: { ids in
            if let id = ids.first, let q = store.quotation(id: id) { store.open(.quotation(q)) }
        }
    }

    @ViewBuilder
    private func challanCell(_ q: Quotation) -> some View {
        if let c = store.challan(forQuotation: q.id) {
            HStack(spacing: 6) {
                Text(c.number).lineLimit(1)
                StatusPill(text: c.status == .approved ? "Approved" : "Pending",
                           color: c.status == .approved ? Theme.green : Theme.orange)
            }
        } else {
            Text("Not created").foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func invoiceCell(_ q: Quotation) -> some View {
        if let inv = store.invoice(forQuotation: q.id) {
            Label(inv.number, systemImage: "checkmark.circle.fill").foregroundStyle(Theme.green).lineLimit(1)
        } else {
            Text("Not created").foregroundStyle(.secondary)
        }
    }
}

struct NextStepButton: View {
    /// Passed in directly: table cells don't reliably inherit environment objects.
    @ObservedObject var store: AppStore
    let quote: Quotation

    var body: some View {
        switch store.stage(of: quote) {
        case .needsChallan:
            Button { store.open(.challan(store.draftChallan(from: quote))) } label: {
                Label("Create DC", systemImage: "truck.box")
            }
            .controlSize(.small)
        case .awaitingApproval:
            Button {
                if let c = store.challan(forQuotation: quote.id) { store.open(.challan(c)) }
            } label: {
                Label("Approve DC", systemImage: "checkmark.seal")
            }
            .controlSize(.small)
            .tint(Theme.orange)
        case .readyToInvoice:
            Button {
                if let c = store.challan(forQuotation: quote.id) {
                    do { store.open(.invoice(try store.draftInvoice(from: c))) } catch { store.report(error) }
                }
            } label: {
                Label("Create Invoice", systemImage: "banknote")
            }
            .controlSize(.small)
            .buttonStyle(.borderedProminent)
            .tint(Theme.green)
        case .invoiced:
            Button {
                if let inv = store.invoice(forQuotation: quote.id) { store.open(.invoice(inv)) }
            } label: {
                Label("View Invoice", systemImage: "doc.richtext")
            }
            .controlSize(.small)
            .buttonStyle(.borderless)
        }
    }
}

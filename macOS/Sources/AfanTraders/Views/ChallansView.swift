import SwiftUI

struct ChallansView: View {
    @EnvironmentObject var store: AppStore
    @State private var search = ""
    @State private var selection: Challan.ID?

    private var rows: [Challan] {
        let q = search.trimmed.lowercased()
        guard !q.isEmpty else { return store.challans }
        return store.challans.filter {
            $0.number.lowercased().contains(q) || $0.poNumber.lowercased().contains(q)
                || $0.customerName.lowercased().contains(q) || $0.vehicleNo.lowercased().contains(q)
        }
    }

    var body: some View {
        Group {
            if store.challans.isEmpty {
                ContentUnavailableView("No delivery challans yet", systemImage: "truck.box",
                                       description: Text("Open a quotation and click “Create Delivery Challan”."))
            } else {
                Table(rows, selection: $selection) {
                    TableColumn("DC No.") { (c: Challan) in Text(c.number).fontWeight(.semibold) }
                        .width(min: 100, ideal: 120)
                    TableColumn("Date") { (c: Challan) in Text(Fmt.date(c.date)) }
                        .width(min: 85, ideal: 95)
                    TableColumn("PO No.") { (c: Challan) in Text(c.poNumber.isEmpty ? "—" : c.poNumber) }
                        .width(min: 80, ideal: 110)
                    TableColumn("Quotation") { (c: Challan) in
                        Text(store.quotation(id: c.quotationId)?.number ?? "—").foregroundStyle(.secondary)
                    }
                    .width(min: 100, ideal: 120)
                    TableColumn("Customer") { (c: Challan) in Text(c.customerName).lineLimit(1) }
                        .width(min: 140, ideal: 200)
                    TableColumn("Vehicle") { (c: Challan) in Text(c.vehicleNo.isEmpty ? "—" : c.vehicleNo) }
                        .width(min: 70, ideal: 90)
                    TableColumn("Status") { (c: Challan) in
                        StatusPill(text: c.status == .approved ? "Approved" : "Pending approval",
                                   color: c.status == .approved ? Theme.green : Theme.orange,
                                   icon: c.status == .approved ? "checkmark" : "clock")
                    }
                    .width(min: 120, ideal: 140)
                    TableColumn("Invoice") { (c: Challan) in
                        if let inv = store.invoice(forChallan: c.id) {
                            Text(inv.number).foregroundStyle(Theme.green)
                        } else if c.status == .approved {
                            Button { createInvoice(c) } label: { Label("Create Invoice", systemImage: "banknote") }
                                .controlSize(.small).buttonStyle(.borderedProminent).tint(Theme.green)
                        } else {
                            Text("—").foregroundStyle(.secondary)
                        }
                    }
                    .width(min: 120, ideal: 140)
                    TableColumn("") { (c: Challan) in
                        Button { store.open(.preview(store.preview(store.printable(c)))) } label: {
                            Image(systemName: "printer")
                        }
                        .buttonStyle(.borderless)
                        .help("Preview / Print")
                    }
                    .width(30)
                }
                .contextMenu(forSelectionType: Challan.ID.self) { ids in
                    if let id = ids.first, let c = store.challan(id: id) {
                        Button("Open Delivery Challan") { store.open(.challan(c)) }
                        Button("Preview / Print") { store.open(.preview(store.preview(store.printable(c)))) }
                        if let q = store.quotation(id: c.quotationId) {
                            Button("Open Quotation \(q.number)") { store.open(.quotation(q)) }
                        }
                    }
                } primaryAction: { ids in
                    if let id = ids.first, let c = store.challan(id: id) { store.open(.challan(c)) }
                }
            }
        }
        .navigationTitle("Delivery Challans")
        .searchable(text: $search, prompt: "Search DC No., PO No., customer or vehicle")
    }

    private func createInvoice(_ c: Challan) {
        do { store.open(.invoice(try store.draftInvoice(from: c))) } catch { store.report(error) }
    }
}

struct InvoicesView: View {
    @EnvironmentObject var store: AppStore
    @State private var search = ""
    @State private var selection: Invoice.ID?

    private var rows: [Invoice] {
        let q = search.trimmed.lowercased()
        guard !q.isEmpty else { return store.invoices }
        return store.invoices.filter {
            $0.number.lowercased().contains(q) || $0.poNumber.lowercased().contains(q)
                || $0.customerName.lowercased().contains(q)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            if store.invoices.isEmpty {
                ContentUnavailableView("No invoices yet", systemImage: "banknote",
                                       description: Text("Approve a delivery challan, then click “Create Invoice”."))
            } else {
                Table(rows, selection: $selection) {
                    TableColumn("Invoice No.") { (i: Invoice) in Text(i.number).fontWeight(.semibold) }
                        .width(min: 100, ideal: 120)
                    TableColumn("Date") { (i: Invoice) in Text(Fmt.date(i.date)) }
                        .width(min: 85, ideal: 95)
                    TableColumn("PO No.") { (i: Invoice) in Text(i.poNumber.isEmpty ? "—" : i.poNumber) }
                        .width(min: 80, ideal: 110)
                    TableColumn("Quotation") { (i: Invoice) in
                        Text(store.quotation(id: i.quotationId)?.number ?? "—").foregroundStyle(.secondary)
                    }
                    .width(min: 100, ideal: 115)
                    TableColumn("DC No.") { (i: Invoice) in
                        Text(store.challan(id: i.challanId)?.number ?? "—").foregroundStyle(.secondary)
                    }
                    .width(min: 100, ideal: 115)
                    TableColumn("Customer") { (i: Invoice) in Text(i.customerName).lineLimit(1) }
                        .width(min: 140, ideal: 200)
                    TableColumn("Subtotal") { (i: Invoice) in
                        Text(Fmt.money(i.subtotal)).monospacedDigit().frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 90, ideal: 110)
                    TableColumn("Sales Tax") { (i: Invoice) in
                        Text(Fmt.money(i.taxAmount)).monospacedDigit().frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 80, ideal: 100)
                    TableColumn("Total") { (i: Invoice) in
                        Text(Fmt.money(i.grandTotal)).fontWeight(.semibold).monospacedDigit()
                            .frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 100, ideal: 120)
                    TableColumn("") { (i: Invoice) in
                        Button { store.open(.preview(store.preview(store.printable(i)))) } label: {
                            Image(systemName: "printer")
                        }
                        .buttonStyle(.borderless)
                        .help("Preview / Print")
                    }
                    .width(30)
                }
                .contextMenu(forSelectionType: Invoice.ID.self) { ids in
                    if let id = ids.first, let inv = store.invoices.first(where: { $0.id == id }) {
                        Button("Open Invoice") { store.open(.invoice(inv)) }
                        Button("Preview / Print") { store.open(.preview(store.preview(store.printable(inv)))) }
                        if let q = store.quotation(id: inv.quotationId) {
                            Button("Open Quotation \(q.number)") { store.open(.quotation(q)) }
                        }
                        if let c = store.challan(id: inv.challanId) {
                            Button("Open Delivery Challan \(c.number)") { store.open(.challan(c)) }
                        }
                    }
                } primaryAction: { ids in
                    if let id = ids.first, let inv = store.invoices.first(where: { $0.id == id }) {
                        store.open(.invoice(inv))
                    }
                }
                Divider()
                HStack(spacing: 24) {
                    Text("\(rows.count) invoice\(rows.count == 1 ? "" : "s")").foregroundStyle(.secondary)
                    Spacer()
                    Text("Sales tax: \(Fmt.money(rows.reduce(0) { $0 + $1.taxAmount }, store.settings.currency))")
                    Text("Total: \(Fmt.money(rows.reduce(0) { $0 + $1.grandTotal }, store.settings.currency))")
                        .fontWeight(.semibold)
                }
                .monospacedDigit()
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
            }
        }
        .navigationTitle("Invoices")
        .searchable(text: $search, prompt: "Search invoice No., PO No. or customer")
    }
}

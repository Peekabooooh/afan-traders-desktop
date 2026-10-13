import SwiftUI

struct ChallanEditor: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State var challan: Challan

    @State private var preview: PreviewDoc?
    @State private var error: String?
    @State private var confirmDelete = false
    @State private var shortageList: [String] = []
    @State private var confirmShortage = false

    private var quotation: Quotation? { store.quotation(id: challan.quotationId) }
    private var invoice: Invoice? { store.invoice(forChallan: challan.id) }
    private var saved: Challan? { challan.id == 0 ? nil : store.challan(id: challan.id) }
    private var isApproved: Bool { saved?.status == .approved }

    var body: some View {
        VStack(spacing: 0) {
            SheetHeader(title: challan.id == 0 ? "New Delivery Challan" : "Delivery Challan \(challan.number)",
                        subtitle: "For the fleet / gate: the material allowed to enter. No prices are shown.",
                        icon: "truck.box", tint: Theme.blue, icon3D: "challans")
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    FlowSteps(quotationSaved: true, challan: saved, invoice: invoice)

                    HStack(alignment: .top, spacing: 16) {
                        VStack(alignment: .leading, spacing: 10) {
                            Label("Deliver To", systemImage: "mappin.and.ellipse").font(.headline)
                            Field("Customer / Company name") {
                                TextField("", text: $challan.customerName).textFieldStyle(.roundedBorder)
                            }
                            Field("Delivery address") {
                                TextField("", text: $challan.customerAddress, axis: .vertical)
                                    .lineLimit(2...3).textFieldStyle(.roundedBorder)
                            }
                            Field("Phone") {
                                TextField("", text: $challan.customerPhone).textFieldStyle(.roundedBorder)
                            }
                        }
                        .card()

                        VStack(alignment: .leading, spacing: 10) {
                            Label("Challan Details", systemImage: "number").font(.headline)
                            Field("Delivery Challan No.") {
                                HStack {
                                    TextField("", text: $challan.number).textFieldStyle(.roundedBorder)
                                    Button { challan.number = store.newChallanNumber() } label: {
                                        Image(systemName: "arrow.triangle.2.circlepath")
                                    }
                                    .help("Generate a new number")
                                }
                            }
                            HStack(spacing: 12) {
                                Field("Date") {
                                    DatePicker("", selection: $challan.date, displayedComponents: .date).labelsHidden()
                                }
                                Field("PO Number") {
                                    Text(challan.poNumber.isEmpty ? "—" : challan.poNumber)
                                        .fontWeight(.semibold)
                                        .frame(maxWidth: .infinity, minHeight: 22, alignment: .leading)
                                }
                                Field("Quotation") {
                                    Text(quotation?.number ?? "—").fontWeight(.semibold)
                                        .frame(maxWidth: .infinity, minHeight: 22, alignment: .leading)
                                }
                            }
                            HStack(spacing: 12) {
                                Field("Vehicle No.") {
                                    TextField("e.g. LES-1234", text: $challan.vehicleNo).textFieldStyle(.roundedBorder)
                                }
                                Field("Driver name") {
                                    TextField("", text: $challan.driverName).textFieldStyle(.roundedBorder)
                                }
                            }
                        }
                        .card()
                        .frame(width: 420)
                    }

                    DocLinesEditor(lines: .constant(challan.lines), mode: .challan, locked: true)

                    HStack(alignment: .top) {
                        Field("Notes (printed on the challan)") {
                            TextEditor(text: $challan.notes)
                                .frame(height: 70)
                                .padding(4)
                                .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor)))
                                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)))
                        }
                        Spacer(minLength: 24)
                        VStack(alignment: .trailing, spacing: 6) {
                            Text("Total quantity").foregroundStyle(.secondary)
                            Text(Fmt.qty(challan.totalQty)).font(.title2.weight(.bold))
                        }
                        .card()
                    }
                    if !isApproved {
                        Label("Approving the challan removes these quantities from stock.", systemImage: "info.circle")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(20)
            }
            Divider()
            footer
        }
        .frame(minWidth: 1000, idealWidth: 1100, minHeight: 680, idealHeight: 780)
        .sheet(item: $preview) { PDFPreviewSheet(doc: $0) }
        .alert("Something went wrong", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(error ?? "") }
        .confirmationDialog("Delete delivery challan \(challan.number)?", isPresented: $confirmDelete) {
            Button("Delete", role: .destructive) {
                do { try store.deleteChallan(challan); dismiss() } catch { self.error = error.localizedDescription }
            }
        } message: {
            Text(isApproved ? "The quantities will be put back into stock." : "")
        }
        .confirmationDialog("Not enough stock", isPresented: $confirmShortage) {
            Button("Approve anyway") { approve(force: true) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(shortageList.joined(separator: "\n") + "\n\nStock will go below zero.")
        }
    }

    private var footer: some View {
        HStack(spacing: 10) {
            if challan.id != 0 && invoice == nil {
                Button(role: .destructive) { confirmDelete = true } label: { Label("Delete", systemImage: "trash") }
            }
            if let saved {
                StatusPill(text: saved.status == .approved ? "Approved" : "Pending approval",
                           color: saved.status == .approved ? Theme.green : Theme.orange,
                           icon: saved.status == .approved ? "checkmark" : "clock")
            }
            Spacer()
            Button { preview = store.preview(store.printable(challan)) } label: {
                Label("Preview / Print", systemImage: "printer")
            }
            if challan.id != 0 && isApproved && invoice == nil {
                Button { setApproved(false) } label: { Label("Mark as Pending", systemImage: "arrow.uturn.backward") }
            }
            if !isApproved {
                Button { approve(force: false) } label: { Label("Save & Approve", systemImage: "checkmark.seal") }
                    .tint(Theme.green)
            }
            if let invoice {
                Button { store.open(.invoice(invoice)) } label: { Label("Open Invoice", systemImage: "banknote") }
            } else if isApproved {
                Button { createInvoice() } label: { Label("Create Invoice", systemImage: "banknote") }
                    .buttonStyle(.borderedProminent).tint(Theme.green)
            }
            Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
            Button { save(close: true) } label: { Label("Save", systemImage: "checkmark") }
                .keyboardShortcut("s", modifiers: .command)
                .buttonStyle(.borderedProminent)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }

    @discardableResult
    private func save(close: Bool) -> Challan? {
        do {
            let s = try store.saveChallan(challan)
            challan = s
            if close { dismiss() }
            return s
        } catch {
            self.error = error.localizedDescription
            return nil
        }
    }

    private func approve(force: Bool) {
        if !force {
            let shortages = store.shortages(for: challan)
            if !shortages.isEmpty {
                shortageList = shortages
                confirmShortage = true
                return
            }
        }
        guard let s = save(close: false) else { return }
        do {
            try store.setApproved(s, true)
            challan = store.challan(id: s.id) ?? s
        } catch { self.error = error.localizedDescription }
    }

    private func setApproved(_ value: Bool) {
        do {
            try store.setApproved(challan, value)
            challan = store.challan(id: challan.id) ?? challan
        } catch { self.error = error.localizedDescription }
    }

    private func createInvoice() {
        guard let s = save(close: false) else { return }
        do { store.open(.invoice(try store.draftInvoice(from: s))) } catch { self.error = error.localizedDescription }
    }
}

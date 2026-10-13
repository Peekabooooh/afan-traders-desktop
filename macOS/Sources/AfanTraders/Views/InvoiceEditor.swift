import SwiftUI

struct InvoiceEditor: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State var invoice: Invoice

    @State private var preview: PreviewDoc?
    @State private var error: String?
    @State private var confirmDelete = false
    @State private var marginPercent: Double = 0

    private var quotation: Quotation? { store.quotation(id: invoice.quotationId) }
    private var challan: Challan? { store.challan(id: invoice.challanId) }

    var body: some View {
        VStack(spacing: 0) {
            SheetHeader(title: invoice.id == 0 ? "New Invoice" : "Invoice \(invoice.number)",
                        subtitle: "Prices start from the quotation – change any rate to add your margin.",
                        icon: "banknote", tint: Theme.green, icon3D: "invoices")
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    FlowSteps(quotationSaved: true, challan: challan, invoice: invoice.id == 0 ? nil : invoice)

                    HStack(alignment: .top, spacing: 16) {
                        VStack(alignment: .leading, spacing: 10) {
                            Label("Bill To", systemImage: "person.crop.rectangle").font(.headline)
                            Field("Customer / Company name") {
                                TextField("", text: $invoice.customerName).textFieldStyle(.roundedBorder)
                            }
                            Field("Address") {
                                TextField("", text: $invoice.customerAddress, axis: .vertical)
                                    .lineLimit(2...3).textFieldStyle(.roundedBorder)
                            }
                            Field("Phone") {
                                TextField("", text: $invoice.customerPhone).textFieldStyle(.roundedBorder)
                            }
                        }
                        .card()

                        VStack(alignment: .leading, spacing: 10) {
                            Label("Invoice Details", systemImage: "number").font(.headline)
                            Field("Invoice No.") {
                                HStack {
                                    TextField("", text: $invoice.number).textFieldStyle(.roundedBorder)
                                    Button { invoice.number = store.newInvoiceNumber() } label: {
                                        Image(systemName: "arrow.triangle.2.circlepath")
                                    }
                                    .help("Generate a new number")
                                }
                            }
                            Field("Date") {
                                DatePicker("", selection: $invoice.date, displayedComponents: .date).labelsHidden()
                            }
                            HStack(spacing: 12) {
                                reference("PO Number", invoice.poNumber)
                                reference("Quotation", quotation?.number ?? "—")
                                reference("Delivery Challan", challan?.number ?? "—")
                            }
                        }
                        .card()
                        .frame(width: 420)
                    }

                    marginTool

                    DocLinesEditor(lines: $invoice.lines, mode: .invoice)

                    HStack(alignment: .top, spacing: 16) {
                        Field("Notes (printed on the invoice)") {
                            TextEditor(text: $invoice.notes)
                                .frame(height: 90)
                                .padding(4)
                                .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor)))
                                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)))
                        }
                        Spacer()
                        TotalsBox(subtotal: invoice.subtotal, taxRate: $invoice.taxRate,
                                  profit: store.profit(of: invoice.lines))
                    }
                }
                .padding(20)
            }
            Divider()
            footer
        }
        .frame(minWidth: 1040, idealWidth: 1140, minHeight: 700, idealHeight: 820)
        .sheet(item: $preview) { PDFPreviewSheet(doc: $0) }
        .alert("Cannot save", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(error ?? "") }
        .confirmationDialog("Delete invoice \(invoice.number)?", isPresented: $confirmDelete) {
            Button("Delete", role: .destructive) {
                do { try store.deleteInvoice(invoice); dismiss() } catch { self.error = error.localizedDescription }
            }
        }
    }

    private func reference(_ label: String, _ value: String) -> some View {
        Field(label) {
            Text(value.isEmpty ? "—" : value).fontWeight(.semibold).lineLimit(1)
                .frame(maxWidth: .infinity, minHeight: 22, alignment: .leading)
        }
    }

    private var quotedSubtotal: Double {
        invoice.lines.reduce(0) { $0 + ($1.baseRate * $1.qty) }.rounded2
    }

    private var marginTool: some View {
        HStack(spacing: 12) {
            Image(systemName: "percent").foregroundStyle(Theme.green)
            Text("Add margin on quotation prices:")
            NumField(value: $marginPercent, width: 70)
            Text("%")
            Button("Apply to all items") {
                for i in invoice.lines.indices {
                    invoice.lines[i].rate = (invoice.lines[i].baseRate * (1 + marginPercent / 100)).rounded2
                }
            }
            Button("Reset to quotation prices") {
                for i in invoice.lines.indices { invoice.lines[i].rate = invoice.lines[i].baseRate }
                marginPercent = 0
            }
            Spacer()
            let diff = (invoice.subtotal - quotedSubtotal).rounded2
            if diff != 0 {
                Text("\(diff > 0 ? "+" : "")\(Fmt.money(diff, store.settings.currency)) vs quotation")
                    .font(.callout.weight(.semibold))
                    .foregroundStyle(diff > 0 ? Theme.green : Theme.red)
            }
        }
        .card(padding: 12)
    }

    private var footer: some View {
        HStack(spacing: 10) {
            if invoice.id != 0 {
                Button(role: .destructive) { confirmDelete = true } label: { Label("Delete", systemImage: "trash") }
            }
            Spacer()
            Button { preview = store.preview(store.printable(invoice)) } label: {
                Label("Preview / Print", systemImage: "printer")
            }
            Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
            Button { save() } label: { Label(invoice.id == 0 ? "Create Invoice" : "Save", systemImage: "checkmark") }
                .keyboardShortcut("s", modifiers: .command)
                .buttonStyle(.borderedProminent)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }

    private func save() {
        do {
            invoice = try store.saveInvoice(invoice)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

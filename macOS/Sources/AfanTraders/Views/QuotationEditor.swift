import SwiftUI

struct QuotationEditor: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State var quote: Quotation

    @State private var preview: PreviewDoc?
    @State private var error: String?
    @State private var confirmDelete = false

    private var challan: Challan? { store.challan(forQuotation: quote.id) }
    private var invoice: Invoice? { store.invoice(forQuotation: quote.id) }
    private var locked: Bool { challan != nil }

    var body: some View {
        VStack(spacing: 0) {
            SheetHeader(title: quote.id == 0 ? "New Quotation" : "Quotation \(quote.number)",
                        subtitle: quote.customerName.isEmpty ? "Fill in the customer and add items" : quote.customerName,
                        icon: "doc.text", icon3D: "quotations")
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    FlowSteps(quotationSaved: quote.id != 0, challan: challan, invoice: invoice)

                    if locked {
                        Label("Items are locked because a delivery challan has been created. You can still change the customer and PO number.",
                              systemImage: "lock.fill")
                            .font(.callout)
                            .foregroundStyle(Theme.orange)
                            .padding(10)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.orange.opacity(0.1)))
                    }

                    HStack(alignment: .top, spacing: 16) {
                        customerCard
                        detailsCard.frame(width: 380)
                    }

                    if !locked {
                        ItemSearchField(onPick: add)
                            .zIndex(10)
                    }
                    DocLinesEditor(lines: $quote.lines, mode: .quotation, locked: locked)

                    HStack(alignment: .top, spacing: 16) {
                        Field("Notes (printed on the quotation)") {
                            TextEditor(text: $quote.notes)
                                .font(.body)
                                .frame(height: 90)
                                .padding(4)
                                .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .textBackgroundColor)))
                                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)))
                        }
                        Spacer()
                        TotalsBox(subtotal: quote.subtotal, taxRate: $quote.taxRate, editableTax: !locked,
                                  profit: store.profit(of: quote.lines))
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
        .confirmationDialog("Delete quotation \(quote.number)?", isPresented: $confirmDelete) {
            Button("Delete", role: .destructive) {
                do { try store.deleteQuotation(quote); dismiss() } catch { self.error = error.localizedDescription }
            }
        }
    }

    private var customerCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label("Customer", systemImage: "person.crop.rectangle").font(.headline)
                Spacer()
                if !store.knownCustomers.isEmpty {
                    Menu {
                        ForEach(store.knownCustomers, id: \.name) { c in
                            Button(c.name) {
                                quote.customerName = c.name
                                quote.customerAddress = c.address
                                quote.customerPhone = c.phone
                            }
                        }
                    } label: {
                        Label("Previous customers", systemImage: "clock.arrow.circlepath")
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                }
            }
            Field("Customer / Company name") {
                TextField("e.g. ABC Industries", text: $quote.customerName).textFieldStyle(.roundedBorder)
            }
            Field("Address") {
                TextField("Street, City", text: $quote.customerAddress, axis: .vertical)
                    .lineLimit(2...3).textFieldStyle(.roundedBorder)
            }
            Field("Phone") {
                TextField("Phone number", text: $quote.customerPhone).textFieldStyle(.roundedBorder)
            }
        }
        .card()
    }

    private var detailsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("Quotation Details", systemImage: "number").font(.headline)
            Field("Quotation No.") {
                HStack {
                    TextField("Number", text: $quote.number).textFieldStyle(.roundedBorder)
                    Button { quote.number = store.newQuotationNumber() } label: { Image(systemName: "arrow.triangle.2.circlepath") }
                        .help("Generate a new number")
                }
            }
            HStack(spacing: 12) {
                Field("Date") {
                    DatePicker("", selection: $quote.date, displayedComponents: .date).labelsHidden()
                }
                Field("PO Number") {
                    TextField("Customer PO", text: $quote.poNumber).textFieldStyle(.roundedBorder)
                }
            }
            Text("The PO number links this quotation, its delivery challan and its invoice.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .card()
    }

    private var footer: some View {
        HStack(spacing: 10) {
            if quote.id != 0 && !locked {
                Button(role: .destructive) { confirmDelete = true } label: { Label("Delete", systemImage: "trash") }
            }
            Spacer()
            Button { preview = store.preview(store.printable(quote)) } label: {
                Label("Preview / Print", systemImage: "printer")
            }
            .disabled(quote.lines.isEmpty)

            if let challan {
                Button { store.open(.challan(challan)) } label: {
                    Label("Open Delivery Challan", systemImage: "truck.box")
                }
            } else {
                Button { saveThenCreateChallan() } label: {
                    Label("Save & Create Delivery Challan", systemImage: "truck.box.badge.clock")
                }
                .disabled(quote.lines.isEmpty)
            }
            Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
            Button { save(close: true) } label: { Label("Save", systemImage: "checkmark") }
                .keyboardShortcut("s", modifiers: .command)
                .buttonStyle(.borderedProminent)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }

    private func add(_ item: Item) {
        if let idx = quote.lines.firstIndex(where: { $0.itemId == item.id }) {
            quote.lines[idx].qty += 1
            return
        }
        quote.lines.append(DocLine(itemId: item.id, code: item.code, name: item.name, details: item.details,
                                   unit: item.unit, qty: 1, rate: item.salePrice, baseRate: item.salePrice))
    }

    @discardableResult
    private func save(close: Bool) -> Quotation? {
        do {
            let saved = try store.saveQuotation(quote)
            quote = saved
            if close { dismiss() }
            return saved
        } catch {
            self.error = error.localizedDescription
            return nil
        }
    }

    private func saveThenCreateChallan() {
        guard let saved = save(close: false) else { return }
        store.open(.challan(store.draftChallan(from: saved)))
    }
}

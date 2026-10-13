import SwiftUI

/// Bulk item adder. One purchase invoice number, many item rows. Saving
/// creates new items and adds the quantities to existing items' stock, and the
/// stock stays linked to this invoice number.
struct PurchaseEditor: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State var purchase: Purchase

    @State private var error: String?
    @State private var confirmDelete = false
    @State private var activeRow: UUID?

    init(purchase: Purchase) {
        var p = purchase
        if p.id == 0 && p.lines.filter({ !$0.isBlank }).count == p.lines.count {
            p.lines += (0..<max(1, 5 - p.lines.count)).map { _ in PurchaseLine(qty: 0) }
        }
        _purchase = State(initialValue: p)
    }

    private var filledLines: [PurchaseLine] { purchase.lines.filter { !$0.isBlank } }
    private var newCount: Int { filledLines.filter { isNew($0) }.count }

    var body: some View {
        VStack(spacing: 0) {
            SheetHeader(title: purchase.id == 0 ? "Add Items / Stock In" : "Purchase Invoice \(purchase.number)",
                        subtitle: "Enter all items from one purchase invoice. Existing Item IDs add to their stock.",
                        icon: "tray.and.arrow.down.fill", tint: Theme.orange, icon3D: "stockin")
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    HStack(alignment: .bottom, spacing: 12) {
                        Field("Purchase invoice No. *") {
                            TextField("Supplier's bill / invoice number", text: $purchase.number)
                                .textFieldStyle(.roundedBorder)
                        }
                        .frame(width: 260)
                        Field("Supplier") {
                            TextField("Supplier name (optional)", text: $purchase.supplier).textFieldStyle(.roundedBorder)
                        }
                        Field("Date") {
                            DatePicker("", selection: $purchase.date, displayedComponents: .date).labelsHidden()
                        }
                        .frame(width: 130)
                    }
                    .card()

                    rowsTable

                    HStack(spacing: 10) {
                        Button { addRows(1) } label: { Label("Add Row", systemImage: "plus") }
                        Button { addRows(5) } label: { Label("Add 5 Rows", systemImage: "plus.square.on.square") }
                        Text("Empty rows are ignored. Picture is optional.").font(.caption).foregroundStyle(.secondary)
                        Spacer()
                    }

                    HStack(alignment: .top) {
                        Field("Notes") {
                            TextField("Optional", text: $purchase.notes).textFieldStyle(.roundedBorder)
                        }
                        .frame(maxWidth: 420)
                        Spacer(minLength: 30)
                        summary
                    }
                }
                .padding(20)
            }
            Divider()
            HStack {
                if purchase.id != 0 {
                    Button(role: .destructive) { confirmDelete = true } label: { Label("Delete", systemImage: "trash") }
                }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button { save() } label: { Label("Save & Add to Stock", systemImage: "tray.and.arrow.down") }
                    .keyboardShortcut("s", modifiers: .command)
                    .buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
        }
        .frame(minWidth: 1180, idealWidth: 1240, minHeight: 640, idealHeight: 800)
        .alert("Cannot save", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(error ?? "") }
        .confirmationDialog("Delete this purchase invoice?", isPresented: $confirmDelete) {
            Button("Delete", role: .destructive) {
                do { try store.deletePurchase(purchase); dismiss() } catch { self.error = error.localizedDescription }
            }
        } message: { Text("The quantities will be removed from stock again.") }
    }

    // MARK: Table

    private var rowsTable: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("#").frame(width: 22, alignment: .leading)
                Text("Item ID *").frame(width: 150, alignment: .leading)
                Text("Name *").frame(maxWidth: .infinity, alignment: .leading)
                Text("Description").frame(maxWidth: .infinity, alignment: .leading)
                Text("Unit").frame(width: 60, alignment: .leading)
                Text("Qty *").frame(width: 70, alignment: .trailing)
                Text("Cost price").frame(width: 95, alignment: .trailing)
                Text("Sale price").frame(width: 95, alignment: .trailing)
                Text("Total").frame(width: 95, alignment: .trailing)
                Text("Pic").frame(width: 32)
                Color.clear.frame(width: 22)
            }
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            Divider()
            ForEach(Array(purchase.lines.enumerated()), id: \.element.id) { idx, line in
                row(idx, line)
                    .background(idx.isMultiple(of: 2) ? Color.clear : Color.primary.opacity(0.03))
                    .zIndex(activeRow == line.id ? 10 : 0)
            }
        }
        .card(padding: 0)
    }

    @ViewBuilder
    private func row(_ idx: Int, _ line: PurchaseLine) -> some View {
        let existing = existingItem(line)
        HStack(alignment: .top, spacing: 8) {
            Text("\(idx + 1)").foregroundStyle(.secondary).frame(width: 22, alignment: .leading).padding(.top, 4)

            VStack(alignment: .leading, spacing: 3) {
                ItemCodeField(code: codeBinding(idx), active: activeBinding(line.id)) { item in fill(idx, from: item) }
                if let existing {
                    Text("In stock \(Fmt.qty(existing.stock)) → \(Fmt.qty(existing.stock + line.qty))")
                        .font(.caption2).foregroundStyle(Theme.blue)
                } else if !line.code.trimmed.isEmpty {
                    StatusPill(text: "NEW ITEM", color: Theme.green)
                }
            }
            .frame(width: 150, alignment: .leading)

            Group {
                if let existing {
                    Text(existing.name).lineLimit(1).padding(.top, 4)
                } else {
                    TextField("Item name", text: binding(idx).name).textFieldStyle(.roundedBorder)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Group {
                if let existing {
                    Text(existing.details.isEmpty ? "—" : existing.details)
                        .foregroundStyle(.secondary).lineLimit(1).padding(.top, 4)
                } else {
                    TextField("Size, brand… (optional)", text: binding(idx).details).textFieldStyle(.roundedBorder)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Group {
                if let existing {
                    Text(existing.unit).foregroundStyle(.secondary).padding(.top, 4)
                } else {
                    TextField("Pcs", text: binding(idx).unit).textFieldStyle(.roundedBorder)
                }
            }
            .frame(width: 60, alignment: .leading)

            NumField(value: binding(idx).qty).frame(width: 70)
            NumField(value: binding(idx).cost).frame(width: 95)
            NumField(value: binding(idx).salePrice).frame(width: 95)
            Text(Fmt.money(line.total)).monospacedDigit().fontWeight(.medium)
                .frame(width: 95, alignment: .trailing).padding(.top, 4)

            pictureButton(idx, line, existing: existing).frame(width: 32)

            Button { removeRow(line.id) } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                .buttonStyle(.plain)
                .help("Remove row")
                .frame(width: 22)
                .padding(.top, 4)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
    }

    @ViewBuilder
    private func pictureButton(_ idx: Int, _ line: PurchaseLine, existing: Item?) -> some View {
        let data = line.imageData ?? existing?.imageData
        Button {
            if let picked = ImageUtil.pickImage() { binding(idx).wrappedValue.imageData = picked }
        } label: {
            if data != nil {
                ItemThumb(data: data, size: 26)
            } else {
                Image(systemName: "photo.badge.plus").foregroundStyle(.secondary).frame(width: 26, height: 26)
            }
        }
        .buttonStyle(.plain)
        .help(data == nil ? "Add a picture (optional)" : "Change picture")
        .contextMenu {
            if line.imageData != nil { Button("Remove picture") { binding(idx).wrappedValue.imageData = nil } }
        }
    }

    private var summary: some View {
        VStack(alignment: .trailing, spacing: 6) {
            HStack(spacing: 18) {
                VStack(alignment: .trailing) {
                    Text("\(filledLines.count)").font(.title3.weight(.bold))
                    Text("items").font(.caption).foregroundStyle(.secondary)
                }
                VStack(alignment: .trailing) {
                    Text("\(newCount)").font(.title3.weight(.bold)).foregroundStyle(Theme.green)
                    Text("new").font(.caption).foregroundStyle(.secondary)
                }
                VStack(alignment: .trailing) {
                    Text(Fmt.qty(filledLines.reduce(0) { $0 + $1.qty })).font(.title3.weight(.bold))
                    Text("total qty").font(.caption).foregroundStyle(.secondary)
                }
                VStack(alignment: .trailing) {
                    Text(Fmt.money(purchase.total, store.settings.currency)).font(.title3.weight(.bold)).monospacedDigit()
                    Text("invoice total (cost)").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .card()
    }

    // MARK: Helpers

    private func existingItem(_ line: PurchaseLine) -> Item? {
        store.item(id: line.itemId) ?? store.item(code: line.code)
    }

    private func isNew(_ line: PurchaseLine) -> Bool { existingItem(line) == nil }

    private func binding(_ idx: Int) -> Binding<PurchaseLine> {
        Binding(
            get: { idx < purchase.lines.count ? purchase.lines[idx] : PurchaseLine() },
            set: { if idx < purchase.lines.count { purchase.lines[idx] = $0 } }
        )
    }

    /// Typing an existing Item ID links the row to that item and fills its prices.
    private func codeBinding(_ idx: Int) -> Binding<String> {
        Binding(
            get: { idx < purchase.lines.count ? purchase.lines[idx].code : "" },
            set: { value in
                guard idx < purchase.lines.count else { return }
                purchase.lines[idx].code = value
                if !value.trimmed.isEmpty && purchase.lines[idx].qty <= 0 { purchase.lines[idx].qty = 1 }
                if let item = store.item(code: value) {
                    if purchase.lines[idx].itemId != item.id { fill(idx, from: item) }
                } else {
                    purchase.lines[idx].itemId = nil
                }
            }
        )
    }

    private func activeBinding(_ id: UUID) -> Binding<Bool> {
        Binding(
            get: { activeRow == id },
            set: { isActive in
                if isActive { activeRow = id } else if activeRow == id { activeRow = nil }
            }
        )
    }

    private func fill(_ idx: Int, from item: Item) {
        guard idx < purchase.lines.count else { return }
        var l = purchase.lines[idx]
        l.itemId = item.id
        l.code = item.code
        l.name = item.name
        l.details = item.details
        l.unit = item.unit
        l.cost = item.costPrice
        l.salePrice = item.salePrice
        if l.qty <= 0 { l.qty = 1 }
        purchase.lines[idx] = l
        if idx == purchase.lines.count - 1 { addRows(1) }
    }

    private func addRows(_ n: Int) {
        purchase.lines += (0..<n).map { _ in PurchaseLine(qty: 0) }
    }

    private func removeRow(_ id: UUID) {
        purchase.lines.removeAll { $0.id == id }
        if purchase.lines.isEmpty { addRows(1) }
    }

    private func save() {
        do {
            try store.savePurchase(purchase)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

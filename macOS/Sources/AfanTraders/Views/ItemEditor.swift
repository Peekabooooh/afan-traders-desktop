import SwiftUI

struct ItemEditor: View {
    @EnvironmentObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State var item: Item

    @State private var error: String?
    @State private var confirmDelete = false

    var body: some View {
        VStack(spacing: 0) {
            SheetHeader(title: item.id == 0 ? "New Item" : "Edit Item",
                        subtitle: item.id == 0 ? "Add a product to your inventory" : item.code,
                        icon: "shippingbox", tint: Theme.navy, icon3D: "inventory")
            Divider()
            HStack(alignment: .top, spacing: 20) {
                VStack(spacing: 10) {
                    ItemThumb(data: item.imageData, size: 150)
                        .overlay(RoundedRectangle(cornerRadius: 30).stroke(Color.primary.opacity(0.1)))
                    HStack {
                        Button(item.imageData == nil ? "Add Picture…" : "Change…") {
                            if let data = ImageUtil.pickImage() { item.imageData = data }
                        }
                        if item.imageData != nil {
                            Button("Remove") { item.imageData = nil }
                        }
                    }
                    Text("Picture is optional").font(.caption).foregroundStyle(.secondary)
                }
                .frame(width: 170)

                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 12) {
                        Field("Item ID *") {
                            TextField("e.g. CBL-2.5MM", text: $item.code).textFieldStyle(.roundedBorder)
                        }
                        .frame(width: 180)
                        Field("Item name *") {
                            TextField("e.g. Copper Cable 2.5mm", text: $item.name).textFieldStyle(.roundedBorder)
                        }
                    }
                    Field("Description") {
                        TextField("Size, brand, model…", text: $item.details, axis: .vertical)
                            .lineLimit(2...4).textFieldStyle(.roundedBorder)
                    }
                    HStack(spacing: 12) {
                        Field("Unit") {
                            TextField("Pcs", text: $item.unit).textFieldStyle(.roundedBorder)
                        }
                        .frame(width: 100)
                        Field("Sale price (\(store.settings.currency))") {
                            NumField(value: $item.salePrice)
                        }
                        Field("Cost price (\(store.settings.currency))") {
                            if store.showCost {
                                NumField(value: $item.costPrice)
                            } else {
                                Button { store.showCost = true } label: {
                                    Label("Hidden – click to show", systemImage: "eye.slash")
                                        .frame(maxWidth: .infinity)
                                }
                            }
                        }
                        Field("In stock") {
                            NumField(value: $item.stock)
                        }
                        .frame(width: 110)
                    }
                    if store.showCost && item.costPrice > 0 && item.salePrice > 0 {
                        let margin = (item.salePrice - item.costPrice) / item.costPrice * 100
                        Label("Margin \(Fmt.qty(margin.rounded2))% · Profit \(Fmt.money(item.salePrice - item.costPrice, store.settings.currency)) per \(item.unit)",
                              systemImage: "chart.line.uptrend.xyaxis")
                            .font(.callout)
                            .foregroundStyle(margin >= 0 ? Theme.green : Theme.red)
                    }
                    Text("Tip: stock is normally added through Purchases. Change “In stock” here only to correct a count.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(20)
            if item.id != 0 { history }
            Spacer(minLength: 0)
            Divider()
            HStack {
                if item.id != 0 {
                    Button(role: .destructive) { confirmDelete = true } label: { Label("Delete", systemImage: "trash") }
                }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button { save() } label: { Label("Save Item", systemImage: "checkmark") }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
        }
        .frame(width: 860, height: item.id == 0 ? 440 : 640)
        .alert("Cannot save", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text(error ?? "") }
        .confirmationDialog("Delete \(item.name)?", isPresented: $confirmDelete) {
            Button("Delete", role: .destructive) {
                do { try store.deleteItem(item); dismiss() } catch { self.error = error.localizedDescription }
            }
        } message: {
            Text("Old quotations and invoices keep their copy of this item.")
        }
    }

    private var history: some View {
        let moves = store.stockHistory(of: item)
        return VStack(alignment: .leading, spacing: 6) {
            Label("Stock History", systemImage: "clock.arrow.circlepath").font(.headline)
            if moves.isEmpty {
                Text("No purchase invoices or deliveries for this item yet.").foregroundStyle(.secondary).font(.callout)
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(moves) { m in
                            HStack {
                                Text(Fmt.date(m.date)).foregroundStyle(.secondary).frame(width: 95, alignment: .leading)
                                Text(m.reference).fontWeight(.medium).frame(width: 230, alignment: .leading)
                                Text(m.party.isEmpty ? "—" : m.party).foregroundStyle(.secondary).lineLimit(1)
                                Spacer()
                                if let cost = m.cost {
                                    Group {
                                        if store.showCost { Text("@ \(Fmt.money(cost))") } else { HiddenCost() }
                                    }
                                    .foregroundStyle(.secondary)
                                }
                                Text(m.qty > 0 ? "+\(Fmt.qty(m.qty))" : Fmt.qty(m.qty))
                                    .fontWeight(.semibold)
                                    .foregroundStyle(m.qty > 0 ? Theme.green : Theme.red)
                                    .frame(width: 70, alignment: .trailing)
                            }
                            .monospacedDigit()
                            .padding(.vertical, 5)
                            Divider().opacity(0.5)
                        }
                    }
                }
                .frame(maxHeight: 170)
            }
        }
        .padding(.horizontal, 20)
        .padding(.bottom, 12)
    }

    private func save() {
        do {
            try store.saveItem(item)
            dismiss()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

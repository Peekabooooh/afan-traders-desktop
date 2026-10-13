import SwiftUI

struct InventoryView: View {
    @EnvironmentObject var store: AppStore
    @State private var search = ""
    @State private var selection: Item.ID?

    private var rows: [Item] {
        let q = search.trimmed.lowercased()
        guard !q.isEmpty else { return store.items }
        return store.items.filter {
            $0.code.lowercased().contains(q) || $0.name.lowercased().contains(q) || $0.details.lowercased().contains(q)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            if store.items.isEmpty {
                ContentUnavailableView {
                    Label("Inventory is empty", systemImage: "shippingbox")
                } description: {
                    Text("Enter a purchase invoice number and add all its items in one go.")
                } actions: {
                    HStack {
                        Button("Add Items / Stock") { store.showCost = true; store.open(.purchase(Purchase())) }
                            .buttonStyle(.borderedProminent)
                    }
                }
            } else {
                Table(rows, selection: $selection) {
                    TableColumn("") { (i: Item) in ItemThumb(data: i.imageData, size: 28) }
                        .width(34)
                    TableColumn("Item ID") { (i: Item) in Text(i.code).fontWeight(.semibold) }
                        .width(min: 90, ideal: 120)
                    TableColumn("Name") { (i: Item) in Text(i.name).lineLimit(1) }
                        .width(min: 140, ideal: 220)
                    TableColumn("Description") { (i: Item) in Text(i.details).foregroundStyle(.secondary).lineLimit(1) }
                        .width(min: 100, ideal: 220)
                    TableColumn("Unit") { (i: Item) in Text(i.unit).foregroundStyle(.secondary) }
                        .width(min: 40, ideal: 50)
                    TableColumn("Last Purchase Inv.") { (i: Item) in
                        Text(store.lastPurchase(of: i)?.number ?? "—").foregroundStyle(.secondary).lineLimit(1)
                    }
                    .width(min: 90, ideal: 120)
                    TableColumn("In Stock") { (i: Item) in
                        HStack {
                            Spacer()
                            if i.stock <= 0 {
                                StatusPill(text: Fmt.qty(i.stock), color: Theme.red)
                            } else {
                                Text(Fmt.qty(i.stock)).monospacedDigit()
                            }
                        }
                    }
                    .width(min: 70, ideal: 85)
                    TableColumn("Sale Price") { (i: Item) in
                        Text(Fmt.money(i.salePrice)).monospacedDigit().frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 90, ideal: 110)
                    TableColumn("Cost Price") { (i: Item) in
                        Group {
                            if store.showCost { Text(Fmt.money(i.costPrice)).monospacedDigit() } else { HiddenCost() }
                        }
                        .frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 90, ideal: 110)
                }
                .contextMenu(forSelectionType: Item.ID.self) { ids in
                    if let id = ids.first, let item = store.item(id: id) {
                        Button("Edit Item") { store.open(.item(item)) }
                        Button("Add Stock (New Purchase Invoice)") {
                            store.showCost = true
                            store.open(.purchase(Purchase(lines: [
                                PurchaseLine(itemId: item.id, code: item.code, name: item.name, details: item.details,
                                             unit: item.unit, qty: 1, cost: item.costPrice, salePrice: item.salePrice)
                            ])))
                        }
                    }
                } primaryAction: { ids in
                    if let id = ids.first, let item = store.item(id: id) { store.open(.item(item)) }
                }
                Divider()
                footer
            }
        }
        .navigationTitle("Inventory")
        .searchable(text: $search, prompt: "Search Item ID, name or description")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { store.showCost = true; store.open(.purchase(Purchase())) } label: {
                    Label("Add Items / Stock", systemImage: "plus")
                }
                .labelStyle(.titleAndIcon)
                .buttonStyle(.borderedProminent)
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 24) {
            Text("\(rows.count) item\(rows.count == 1 ? "" : "s")").foregroundStyle(.secondary)
            let out = rows.filter { $0.stock <= 0 }.count
            if out > 0 {
                Label("\(out) out of stock", systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.red)
            }
            Spacer()
            Text("Stock value (sale): \(Fmt.money(rows.reduce(0) { $0 + max(0, $1.stock) * $1.salePrice }, store.settings.currency))")
            if store.showCost {
                Text("Stock value (cost): \(Fmt.money(rows.reduce(0) { $0 + max(0, $1.stock) * $1.costPrice }, store.settings.currency))")
                    .fontWeight(.semibold)
            }
        }
        .monospacedDigit()
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }
}

struct PurchasesView: View {
    @EnvironmentObject var store: AppStore
    @State private var selection: Purchase.ID?

    var body: some View {
        Group {
            if !store.showCost {
                ContentUnavailableView {
                    Label("Purchases show cost prices", systemImage: "eye.slash")
                } description: {
                    Text("Cost prices are hidden right now. Show them to view or enter purchases.")
                } actions: {
                    Button("Show Cost Prices") { store.showCost = true }.buttonStyle(.borderedProminent)
                }
            } else if store.purchases.isEmpty {
                ContentUnavailableView {
                    Label("No purchases yet", systemImage: "cart")
                } description: {
                    Text("Enter a supplier's purchase invoice to add items to stock.")
                } actions: {
                    Button("Add Items / Stock") { store.open(.purchase(Purchase())) }.buttonStyle(.borderedProminent)
                }
            } else {
                Table(store.purchases, selection: $selection) {
                    TableColumn("Date") { (p: Purchase) in Text(Fmt.date(p.date)) }
                        .width(min: 85, ideal: 100)
                    TableColumn("Supplier") { (p: Purchase) in Text(p.supplier.isEmpty ? "—" : p.supplier) }
                        .width(min: 140, ideal: 240)
                    TableColumn("Invoice No.") { (p: Purchase) in Text(p.number.isEmpty ? "—" : p.number).fontWeight(.semibold) }
                        .width(min: 110, ideal: 150)
                    TableColumn("Items") { (p: Purchase) in
                        Text(p.lines.map(\.code).joined(separator: ", ")).foregroundStyle(.secondary).lineLimit(1)
                    }
                    .width(min: 150, ideal: 280)
                    TableColumn("Total Cost") { (p: Purchase) in
                        Text(Fmt.money(p.total)).fontWeight(.semibold).monospacedDigit()
                            .frame(maxWidth: .infinity, alignment: .trailing)
                    }
                    .width(min: 100, ideal: 130)
                }
                .contextMenu(forSelectionType: Purchase.ID.self) { ids in
                    if let id = ids.first, let p = store.purchases.first(where: { $0.id == id }) {
                        Button("Open Purchase") { store.open(.purchase(p)) }
                    }
                } primaryAction: { ids in
                    if let id = ids.first, let p = store.purchases.first(where: { $0.id == id }) {
                        store.open(.purchase(p))
                    }
                }
            }
        }
        .navigationTitle("Purchases")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    store.showCost = true
                    store.open(.purchase(Purchase()))
                } label: { Label("Add Items / Stock", systemImage: "plus") }
                    .labelStyle(.titleAndIcon)
            }
        }
    }
}

import SwiftUI

enum AppSection: String, CaseIterable, Identifiable {
    case dashboard, quotations, challans, invoices, inventory, purchases, reports, settings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .dashboard: return "Dashboard"
        case .quotations: return "Quotations"
        case .challans: return "Delivery Challans"
        case .invoices: return "Invoices"
        case .inventory: return "Inventory"
        case .purchases: return "Purchase Invoices"
        case .reports: return "Reports"
        case .settings: return "Settings"
        }
    }

    var icon3D: String {
        switch self {
        case .dashboard: return "dashboard"
        case .quotations: return "quotations"
        case .challans: return "challans"
        case .invoices: return "invoices"
        case .inventory: return "inventory"
        case .purchases: return "purchases"
        case .reports: return "reports"
        case .settings: return "settings"
        }
    }

    var icon: String {
        switch self {
        case .dashboard: return "square.grid.2x2"
        case .quotations: return "doc.text"
        case .challans: return "truck.box"
        case .invoices: return "banknote"
        case .inventory: return "shippingbox"
        case .purchases: return "cart"
        case .reports: return "chart.bar.xaxis"
        case .settings: return "gearshape"
        }
    }
}

struct RootView: View {
    @EnvironmentObject var store: AppStore

    var body: some View {
        NavigationSplitView {
            List {
                Section("Sales") {
                    row(.dashboard)
                    row(.quotations, badge: store.quotations.filter { store.stage(of: $0) != .invoiced }.count)
                    row(.challans, badge: store.challans.filter { $0.status == .pending }.count)
                    row(.invoices)
                }
                Section("Stock") {
                    row(.inventory)
                    row(.purchases)
                }
                Section("Analytics") {
                    row(.reports)
                }
                Section("System") {
                    row(.settings)
                }
            }
            .navigationSplitViewColumnWidth(min: 200, ideal: 220)
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 0) {
                    SidebarOfficeStatus(lan: store.lan) { store.section = .settings }
                        .padding(.horizontal, 10)
                    SidebarSyncStatus(sync: store.sync) { store.section = .settings }
                        .padding(.horizontal, 10)
                    costBadge
                }
            }
        } detail: {
            switch store.section {
            case .dashboard: DashboardView()
            case .quotations: QuotationsView()
            case .challans: ChallansView()
            case .invoices: InvoicesView()
            case .inventory: InventoryView()
            case .purchases: PurchasesView()
            case .reports: ReportsView()
            case .settings: SettingsView()
            }
        }
        .toolbar {
            ToolbarItem(placement: .navigation) { CostToggleButton() }
        }
        .sheet(item: $store.route) { route in
            Group {
                switch route.kind {
                case .item(let i): ItemEditor(item: i)
                case .purchase(let p): PurchaseEditor(purchase: p)
                case .quotation(let q): QuotationEditor(quote: q)
                case .challan(let c): ChallanEditor(challan: c)
                case .invoice(let inv): InvoiceEditor(invoice: inv)
                case .preview(let doc): PDFPreviewSheet(doc: doc)
                }
            }
            .environmentObject(store)
            .id(route.id)
        }
        .alert(store.alertMessage ?? "", isPresented: Binding(get: { store.alertMessage != nil },
                                                             set: { if !$0 { store.alertMessage = nil } })) {
            Button("OK", role: .cancel) {}
        }
    }

    private func row(_ s: AppSection, badge: Int = 0) -> some View {
        let selected = store.section == s
        return Button { store.section = s } label: {
            HStack(spacing: 10) {
                Icon3D(name: s.icon3D, fallback: s.icon, size: 22)
                Text(s.title)
                    .font(.system(size: 13, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? Color.white : Color.primary)
                Spacer(minLength: 4)
                if badge > 0 {
                    Text("\(badge)")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(selected ? Color.accentColor : Color.white)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 1)
                        .background(Capsule().fill(selected ? Color.white : Theme.orange))
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 7).fill(selected ? Color.accentColor : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .listRowInsets(EdgeInsets(top: 1, leading: 6, bottom: 1, trailing: 6))
    }

    private var costBadge: some View {
        Button { store.showCost.toggle() } label: {
            HStack {
                Image(systemName: store.showCost ? "eye.fill" : "eye.slash.fill")
                Text(store.showCost ? "Cost prices visible" : "Cost prices hidden")
                Spacer()
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(store.showCost ? Theme.orange : Color.secondary)
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 8).fill((store.showCost ? Theme.orange : Color.secondary).opacity(0.12)))
        }
        .buttonStyle(.plain)
        .padding(10)
    }
}

import SwiftUI

struct DashboardView: View {
    @EnvironmentObject var store: AppStore

    private func count(_ stage: OrderStage) -> Int {
        store.quotations.filter { store.stage(of: $0) == stage }.count
    }

    private var monthInvoices: [Invoice] {
        let cal = Calendar.current
        return store.invoices.filter { cal.isDate($0.date, equalTo: Date(), toGranularity: .month) }
    }

    private var openQuotes: [Quotation] {
        store.quotations.filter { store.stage(of: $0) != .invoiced }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(store.settings.companyName).font(.largeTitle.weight(.bold)).foregroundStyle(Theme.navy)
                        Text(Date().formatted(date: .complete, time: .omitted)).foregroundStyle(.secondary)
                    }
                    Spacer()
                    HStack(spacing: 10) {
                        Button { store.showCost = true; store.open(.purchase(Purchase())) } label: {
                            Label("Add Items / Stock", systemImage: "tray.and.arrow.down")
                        }
                        Button { store.open(.quotation(store.draftQuotation())) } label: {
                            Label("New Quotation", systemImage: "doc.badge.plus")
                        }
                        .buttonStyle(.borderedProminent)
                    }
                    .controlSize(.large)
                }

                LazyVGrid(columns: [GridItem(.adaptive(minimum: 210), spacing: 14)], spacing: 14) {
                    StatCard(title: "Needs Delivery Challan", value: "\(count(.needsChallan))",
                             icon: OrderStage.needsChallan.icon, color: Theme.blue, icon3D: "challans") { go(.needsChallan) }
                    StatCard(title: "Challan Pending Approval", value: "\(count(.awaitingApproval))",
                             icon: OrderStage.awaitingApproval.icon, color: Theme.orange, icon3D: "pending") { go(.awaitingApproval) }
                    StatCard(title: "Ready to Invoice", value: "\(count(.readyToInvoice))",
                             icon: OrderStage.readyToInvoice.icon, color: Theme.green, icon3D: "ready") { go(.readyToInvoice) }
                    StatCard(title: "Invoiced This Month",
                             value: Fmt.money(monthInvoices.reduce(0) { $0 + $1.grandTotal }, store.settings.currency),
                             icon: "chart.bar.fill", color: Theme.navy, icon3D: "money") {
                        store.section = .invoices
                    }
                    StatCard(title: "Items Out of Stock", value: "\(store.items.filter { $0.stock <= 0 }.count)",
                             icon: "exclamationmark.triangle.fill", color: Theme.red, icon3D: "warning") {
                        store.section = .inventory
                    }
                }

                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("Open Orders").font(.title3.weight(.semibold))
                        Text("Quotations that are not invoiced yet").foregroundStyle(.secondary)
                        Spacer()
                        Button("See all quotations") { go(nil) }.buttonStyle(.link)
                    }
                    if openQuotes.isEmpty {
                        Text("Nothing pending. 🎉").foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity).padding(30).card()
                    } else {
                        QuotationTable(quotes: openQuotes)
                            .frame(minHeight: 320)
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.primary.opacity(0.08)))
                    }
                }
            }
            .padding(24)
        }
        .navigationTitle("Dashboard")
    }

    private func go(_ stage: OrderStage?) {
        store.quotationFilter = stage
        store.section = .quotations
    }
}

struct StatCard: View {
    let title: String
    let value: String
    let icon: String
    let color: Color
    var icon3D: String? = nil
    var action: () -> Void = {}
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                if let icon3D, Icon3D.image(icon3D) != nil {
                    Icon3D(name: icon3D, size: 46)
                        .padding(4)
                        .background(RoundedRectangle(cornerRadius: 12).fill(color.opacity(0.12)))
                } else {
                    Image(systemName: icon)
                        .font(.system(size: 20, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 44, height: 44)
                        .background(RoundedRectangle(cornerRadius: 11).fill(color.gradient))
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(value).font(.title2.weight(.bold)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.6)
                    Text(title).font(.callout).foregroundStyle(.secondary).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .card(padding: 14)
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(color.opacity(hover ? 0.5 : 0), lineWidth: 1.5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

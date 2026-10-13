import SwiftUI
import Charts
import UniformTypeIdentifiers

enum ReportKind: String, CaseIterable, Identifiable {
    case overview = "Overview"
    case daily = "Day by Day"
    case bills = "Bill by Bill"
    case lines = "Line Detail"
    case items = "Item by Item"
    case customers = "Customers"
    case orders = "Orders"
    case purchases = "Purchases"
    case stock = "Stock Value"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .overview: return "reports"
        case .daily: return "pending"
        case .bills: return "invoices"
        case .lines: return "quotations"
        case .items: return "inventory"
        case .customers: return "customer"
        case .orders: return "challans"
        case .purchases: return "purchases"
        case .stock: return "money"
        }
    }
}

struct ReportsView: View {
    @EnvironmentObject var store: AppStore
    @State private var kind: ReportKind = .overview
    @State private var preset: RangePreset = .thisMonth
    @State private var from = Calendar.current.date(byAdding: .day, value: -30, to: Date())!
    @State private var to = Date()
    @State private var search = ""
    @State private var sortColumn: Int?
    @State private var ascending = false
    @State private var preview: PreviewDoc?

    private var range: DateInterval { preset.interval(from: from, to: to) }

    private var periodText: String {
        switch preset {
        case .allTime: return "All time"
        default:
            let r = range
            let start = Fmt.date(r.start), end = Fmt.date(r.end)
            return start == end ? start : "\(start) to \(end)"
        }
    }

    private var table: ReportTable? {
        switch kind {
        case .overview: return nil
        case .daily: return store.dailyReport(range)
        case .bills: return store.billsReport(range)
        case .lines: return store.lineReport(range)
        case .items: return store.itemsReport(range)
        case .customers: return store.customersReport(range)
        case .orders: return store.ordersReport(range)
        case .purchases: return store.purchasesReport(range)
        case .stock: return store.stockReport(range)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            tabBar
            Divider()
            filterBar
            Divider()
            if !store.showCost {
                HStack(spacing: 8) {
                    Image(systemName: "eye.slash")
                    Text("Cost and profit are hidden.")
                    Button("Show Cost & Profit") { store.showCost = true }.buttonStyle(.link)
                    Spacer()
                }
                .font(.callout)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 16)
                .padding(.vertical, 6)
                .background(Color.primary.opacity(0.04))
            }
            if let table {
                let rows = table.filteredSorted(search: search, sort: sortColumn, ascending: ascending)
                ReportTableView(table: table, rows: rows, sortColumn: $sortColumn, ascending: $ascending)
            } else {
                OverviewReport(overview: store.overview(range), periodText: periodText)
            }
        }
        .navigationTitle("Reports")
        .onChange(of: kind) { _, _ in sortColumn = nil; search = "" }
        .sheet(item: $preview) { PDFPreviewSheet(doc: $0) }
    }

    private var tabBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(ReportKind.allCases) { k in
                    Button { kind = k } label: {
                        HStack(spacing: 6) {
                            Icon3D(name: k.icon, size: 18)
                            Text(k.rawValue).font(.callout.weight(kind == k ? .semibold : .regular))
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .foregroundStyle(kind == k ? Color.white : Color.primary)
                        .background(Capsule().fill(kind == k ? Color.accentColor : Color.primary.opacity(0.06)))
                        .contentShape(Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
    }

    private var filterBar: some View {
        HStack(spacing: 10) {
            Picker("Period", selection: $preset) {
                ForEach(RangePreset.allCases) { Text($0.rawValue).tag($0) }
            }
            .frame(width: 190)
            if preset == .custom {
                DatePicker("From", selection: $from, displayedComponents: .date)
                DatePicker("To", selection: $to, displayedComponents: .date)
            } else {
                Text(periodText).foregroundStyle(.secondary)
            }
            Spacer()
            if kind != .overview {
                HStack(spacing: 4) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Filter rows (customer, item, number…)", text: $search).textFieldStyle(.plain)
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 5)
                .frame(width: 260)
                .background(RoundedRectangle(cornerRadius: 7).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.12)))
                Button { exportCSV() } label: { Label("Export to Excel", systemImage: "tablecells") }
                Button { printReport() } label: { Label("Print / PDF", systemImage: "printer") }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }

    private func currentRows() -> (ReportTable, [ReportRow])? {
        guard let t = table else { return nil }
        return (t, t.filteredSorted(search: search, sort: sortColumn, ascending: ascending))
    }

    private func exportCSV() {
        guard let (t, rows) = currentRows() else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "\(t.title.components(separatedBy: " (").first ?? t.title) \(periodText).csv"
            .replacingOccurrences(of: "/", with: "-")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let text = "\u{FEFF}" + t.csv(rows: rows, includeSensitive: store.showCost)
        do { try text.write(to: url, atomically: true, encoding: .utf8) } catch { store.report(error) }
    }

    private func printReport() {
        guard let (t, rows) = currentRows() else { return }
        let data = ReportPDF.render(t, rows: rows, period: periodText, settings: store.settings, showCost: store.showCost)
        preview = PreviewDoc(title: t.title, fileName: "\(t.title) \(periodText).pdf".replacingOccurrences(of: "/", with: "-"),
                             data: data)
    }
}

// MARK: - Overview

private struct OverviewReport: View {
    @EnvironmentObject var store: AppStore
    let overview: Overview
    let periodText: String

    private var cur: String { store.settings.currency }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 210), spacing: 12)], spacing: 12) {
                    tile("Net Sales", Fmt.money(overview.netSales, cur), "before sales tax", icon: "invoices")
                    tile("Sales Tax Collected", Fmt.money(overview.tax, cur), "\(overview.bills) bills", icon: "quotations")
                    tile("Total Billed", Fmt.money(overview.total, cur), "avg \(Fmt.money(overview.avgBill, cur)) per bill", icon: "money")
                    tile("Cost of Goods Sold", Fmt.money(overview.cost, cur), "\(Fmt.qty(overview.qty)) units sold",
                         icon: "inventory", sensitive: true)
                    tile("Gross Profit", Fmt.money(overview.profit, cur),
                         overview.margin.map { "margin \(Fmt.qty($0.rounded2))%" } ?? "—", icon: "reports", sensitive: true,
                         negative: overview.profit < 0)
                    tile("Purchases (Stock In)", Fmt.money(overview.purchases, cur), "\(overview.purchaseBills) purchase invoices",
                         icon: "purchases", sensitive: true)
                    tile("Open Quotations", Fmt.money(overview.openQuotesValue, cur), "\(overview.openQuotes) not invoiced yet",
                         icon: "pending")
                    tile("Stock Value", Fmt.money(overview.stockCost, cur), "at cost · \(Fmt.money(overview.stockSale, cur)) at sale",
                         icon: "stockin", sensitive: true)
                }

                SalesChart(points: overview.points, byMonth: overview.byMonth, showProfit: store.showCost)
                    .frame(height: 300)
                    .card()

                HStack(alignment: .top, spacing: 16) {
                    ranking("Top Items by Sales", overview.topItems)
                    ranking("Top Customers by Sales", overview.topCustomers)
                }
            }
            .padding(20)
        }
    }

    private func tile(_ title: String, _ value: String, _ sub: String, icon: String,
                      sensitive: Bool = false, negative: Bool = false) -> some View {
        let hidden = sensitive && !store.showCost
        return HStack(spacing: 12) {
            Icon3D(name: icon, size: 38)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Text(hidden ? "••••••" : value).font(.title3.weight(.bold)).monospacedDigit()
                    .foregroundStyle(hidden ? Color.secondary : (negative ? Theme.red : Color.primary))
                    .lineLimit(1).minimumScaleFactor(0.6)
                Text(hidden ? "hidden" : sub).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .card(padding: 12)
    }

    private func ranking(_ title: String, _ data: [(name: String, sales: Double, profit: Double)]) -> some View {
        let top = data.map(\.sales).max() ?? 1
        return VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            if data.isEmpty {
                Text("No sales in this period.").foregroundStyle(.secondary)
            }
            ForEach(Array(data.enumerated()), id: \.offset) { _, d in
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text(d.name).lineLimit(1)
                        Spacer()
                        Text(Fmt.money(d.sales, cur)).monospacedDigit().fontWeight(.medium)
                        if store.showCost {
                            Text("profit \(Fmt.money(d.profit))").font(.caption).monospacedDigit()
                                .foregroundStyle(d.profit < 0 ? Theme.red : .secondary)
                        }
                    }
                    .font(.callout)
                    GeometryReader { g in
                        RoundedRectangle(cornerRadius: 2)
                            .fill(ChartColors.sales)
                            .frame(width: max(4, g.size.width * d.sales / max(top, 1)))
                    }
                    .frame(height: 6)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

enum ChartColors {
    /// Validated categorical slots 1 and 2 (light / dark).
    static let sales = dynamic(light: (0x2a, 0x78, 0xd6), dark: (0x39, 0x87, 0xe5))
    static let profit = dynamic(light: (0xeb, 0x68, 0x34), dark: (0xd9, 0x59, 0x26))

    private static func dynamic(light: (Int, Int, Int), dark: (Int, Int, Int)) -> Color {
        Color(nsColor: NSColor(name: nil) { app in
            let c = app.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat(c.0) / 255, green: CGFloat(c.1) / 255, blue: CGFloat(c.2) / 255, alpha: 1)
        })
    }
}

private struct SalesChart: View {
    @EnvironmentObject var store: AppStore
    let points: [ChartPoint]
    let byMonth: Bool
    let showProfit: Bool
    @State private var hover: Date?

    private var unit: Calendar.Component { byMonth ? .month : .day }

    private var hovered: ChartPoint? {
        guard let hover else { return nil }
        return points.first { Calendar.current.isDate($0.date, equalTo: hover, toGranularity: unit) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(showProfit ? "Net sales and profit, \(byMonth ? "by month" : "by day")" : "Net sales, \(byMonth ? "by month" : "by day")")
                .font(.headline)
            if points.isEmpty {
                Text("No sales in this period.").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Chart {
                    ForEach(points) { p in
                        BarMark(x: .value("Date", p.date, unit: unit), y: .value("Amount", p.sales))
                            .foregroundStyle(by: .value("Series", "Net Sales"))
                            .position(by: .value("Series", "Net Sales"))
                            .cornerRadius(3)
                        if showProfit {
                            BarMark(x: .value("Date", p.date, unit: unit), y: .value("Amount", p.profit))
                                .foregroundStyle(by: .value("Series", "Profit"))
                                .position(by: .value("Series", "Profit"))
                                .cornerRadius(3)
                        }
                    }
                    if let h = hovered {
                        RuleMark(x: .value("Date", h.date, unit: unit))
                            .foregroundStyle(Color.secondary.opacity(0.35))
                            .lineStyle(StrokeStyle(lineWidth: 1))
                            .annotation(position: .top, alignment: .center, spacing: 4,
                                        overflowResolution: .init(x: .fit(to: .chart), y: .disabled)) {
                                tooltip(h)
                            }
                    }
                }
                .chartForegroundStyleScale(["Net Sales": ChartColors.sales, "Profit": ChartColors.profit])
                .chartLegend(showProfit ? .visible : .hidden)
                .chartLegend(position: .top, alignment: .trailing)
                .chartYAxis {
                    AxisMarks { value in
                        AxisGridLine().foregroundStyle(Color.primary.opacity(0.08))
                        AxisValueLabel { if let v = value.as(Double.self) { Text(compact(v)) } }
                    }
                }
                .chartXAxis {
                    AxisMarks(values: .automatic(desiredCount: 8)) { _ in
                        AxisValueLabel(format: byMonth ? .dateTime.month(.abbreviated).year(.twoDigits) : .dateTime.day().month(.abbreviated))
                    }
                }
                .chartOverlay { proxy in
                    GeometryReader { geo in
                        Rectangle().fill(.clear).contentShape(Rectangle())
                            .onContinuousHover { phase in
                                switch phase {
                                case .active(let loc):
                                    guard let plot = proxy.plotFrame else { return }
                                    hover = proxy.value(atX: loc.x - geo[plot].origin.x, as: Date.self)
                                case .ended:
                                    hover = nil
                                }
                            }
                    }
                }
            }
        }
    }

    private func tooltip(_ p: ChartPoint) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(byMonth ? p.date.formatted(.dateTime.month(.wide).year()) : Fmt.date(p.date))
                .font(.caption.weight(.semibold))
            HStack(spacing: 6) {
                RoundedRectangle(cornerRadius: 2).fill(ChartColors.sales).frame(width: 8, height: 8)
                Text("Net sales").font(.caption).foregroundStyle(.secondary)
                Spacer(minLength: 10)
                Text(Fmt.money(p.sales, store.settings.currency)).font(.caption.weight(.semibold)).monospacedDigit()
            }
            if showProfit {
                HStack(spacing: 6) {
                    RoundedRectangle(cornerRadius: 2).fill(ChartColors.profit).frame(width: 8, height: 8)
                    Text("Profit").font(.caption).foregroundStyle(.secondary)
                    Spacer(minLength: 10)
                    Text(Fmt.money(p.profit, store.settings.currency)).font(.caption.weight(.semibold)).monospacedDigit()
                }
            }
        }
        .padding(8)
        .frame(width: 210)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .windowBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.12), radius: 6, y: 3)
    }

    private func compact(_ v: Double) -> String {
        let a = abs(v)
        if a >= 1_000_000 { return String(format: "%.1fM", v / 1_000_000) }
        if a >= 1_000 { return String(format: "%.0fK", v / 1_000) }
        return String(format: "%.0f", v)
    }
}

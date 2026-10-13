import Foundation

/// One sold line on an invoice, with its cost.
struct SaleLine {
    let invoice: Invoice
    let line: DocLine
    let unitCost: Double

    var amount: Double { line.total }
    var cost: Double { (unitCost * line.qty).rounded2 }
    var profit: Double { (amount - cost).rounded2 }
}

struct ChartPoint: Identifiable {
    var id: Date { date }
    let date: Date
    var sales: Double
    var profit: Double
}

struct Overview {
    var netSales = 0.0, tax = 0.0, total = 0.0, cost = 0.0, profit = 0.0
    var bills = 0, qty = 0.0, purchases = 0.0, purchaseBills = 0
    var openQuotes = 0, openQuotesValue = 0.0
    var stockCost = 0.0, stockSale = 0.0
    var points: [ChartPoint] = []
    var byMonth = false
    var topItems: [(name: String, sales: Double, profit: Double)] = []
    var topCustomers: [(name: String, sales: Double, profit: Double)] = []

    var margin: Double? { ReportTable.margin(profit: profit, sales: netSales) }
    var avgBill: Double { bills == 0 ? 0 : total / Double(bills) }
}

@MainActor
extension AppStore {
    // MARK: Basics

    func unitCost(_ l: DocLine) -> Double {
        l.cost > 0 ? l.cost : (item(id: l.itemId)?.costPrice ?? 0)
    }

    func invoices(in r: DateInterval) -> [Invoice] { invoices.filter { r.contains($0.date) } }
    func purchases(in r: DateInterval) -> [Purchase] { purchases.filter { r.contains($0.date) } }

    func saleLines(in r: DateInterval) -> [SaleLine] {
        invoices(in: r).flatMap { inv in inv.lines.map { SaleLine(invoice: inv, line: $0, unitCost: unitCost($0)) } }
    }

    func cost(of inv: Invoice) -> Double {
        inv.lines.reduce(0) { $0 + unitCost($1) * $1.qty }.rounded2
    }

    private func key(_ l: DocLine) -> String { l.itemId.map { "#\($0)" } ?? l.code.lowercased() }

    // MARK: Overview

    func overview(_ r: DateInterval) -> Overview {
        var o = Overview()
        let invs = invoices(in: r)
        let lines = saleLines(in: r)
        o.bills = invs.count
        o.netSales = invs.reduce(0) { $0 + $1.subtotal }
        o.tax = invs.reduce(0) { $0 + $1.taxAmount }
        o.total = invs.reduce(0) { $0 + $1.grandTotal }
        o.cost = lines.reduce(0) { $0 + $1.cost }
        o.profit = o.netSales - o.cost
        o.qty = lines.reduce(0) { $0 + $1.line.qty }
        let ps = purchases(in: r)
        o.purchases = ps.reduce(0) { $0 + $1.total }
        o.purchaseBills = ps.count
        let open = quotations.filter { stage(of: $0) != .invoiced }
        o.openQuotes = open.count
        o.openQuotesValue = open.reduce(0) { $0 + $1.subtotal }
        o.stockCost = items.reduce(0) { $0 + max(0, $1.stock) * $1.costPrice }
        o.stockSale = items.reduce(0) { $0 + max(0, $1.stock) * $1.salePrice }

        // Chart: by day, or by month for long ranges.
        let cal = Calendar.current
        let first = invs.map(\.date).min() ?? r.start
        let span = max(r.start, first).distance(to: min(r.end, Date())) / 86400
        o.byMonth = span > 62
        var map: [Date: ChartPoint] = [:]
        for l in lines {
            let d = o.byMonth ? cal.dateInterval(of: .month, for: l.invoice.date)!.start : cal.startOfDay(for: l.invoice.date)
            map[d, default: ChartPoint(date: d, sales: 0, profit: 0)].sales += l.amount
            map[d, default: ChartPoint(date: d, sales: 0, profit: 0)].profit += l.profit
        }
        o.points = map.values.sorted { $0.date < $1.date }

        var itemAgg: [String: (String, Double, Double)] = [:]
        for l in lines {
            let k = key(l.line)
            let cur = itemAgg[k] ?? ("\(l.line.code) – \(l.line.name)", 0, 0)
            itemAgg[k] = (cur.0, cur.1 + l.amount, cur.2 + l.profit)
        }
        o.topItems = itemAgg.values.sorted { $0.1 > $1.1 }.prefix(6).map { ($0.0, $0.1, $0.2) }

        var custAgg: [String: (String, Double, Double)] = [:]
        for inv in invs {
            let k = inv.customerName.lowercased()
            let cur = custAgg[k] ?? (inv.customerName, 0, 0)
            custAgg[k] = (cur.0, cur.1 + inv.subtotal, cur.2 + inv.subtotal - cost(of: inv))
        }
        o.topCustomers = custAgg.values.sorted { $0.1 > $1.1 }.prefix(6).map { ($0.0, $0.1, $0.2) }
        return o
    }

    // MARK: Day by day

    func dailyReport(_ r: DateInterval) -> ReportTable {
        let cal = Calendar.current
        var days: [Date: (bills: Int, qty: Double, net: Double, tax: Double, total: Double, cost: Double, purch: Double)] = [:]
        for inv in invoices(in: r) {
            let d = cal.startOfDay(for: inv.date)
            var v = days[d] ?? (0, 0, 0, 0, 0, 0, 0)
            v.bills += 1
            v.qty += inv.lines.reduce(0) { $0 + $1.qty }
            v.net += inv.subtotal
            v.tax += inv.taxAmount
            v.total += inv.grandTotal
            v.cost += cost(of: inv)
            days[d] = v
        }
        for p in purchases(in: r) {
            let d = cal.startOfDay(for: p.date)
            var v = days[d] ?? (0, 0, 0, 0, 0, 0, 0)
            v.purch += p.total
            days[d] = v
        }
        let rows = days.sorted { $0.key > $1.key }.map { d, v in
            ReportRow(cells: [.date(d), .count(v.bills), .qty(v.qty), .money(v.net), .money(v.tax), .money(v.total),
                              .money(v.cost), .money(v.net - v.cost),
                              .percent(ReportTable.margin(profit: v.net - v.cost, sales: v.net)), .money(v.purch)])
        }
        return ReportTable(title: "Day by Day", columns: [
            ReportColumn(title: "Date", kind: .date, total: .label("TOTAL")),
            ReportColumn(title: "Bills", width: 60, kind: .count, total: .sum),
            ReportColumn(title: "Qty Sold", width: 80, kind: .qty, total: .sum),
            ReportColumn(title: "Net Sales", kind: .money, total: .sum),
            ReportColumn(title: "Sales Tax", kind: .money, total: .sum),
            ReportColumn(title: "Total Billed", width: 120, kind: .money, total: .sum),
            ReportColumn(title: "Cost", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Profit", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Margin", width: 80, kind: .percent, sensitive: true, total: .ratio(num: 7, den: 3, percent: true)),
            ReportColumn(title: "Purchases", kind: .money, sensitive: true, total: .sum),
        ], rows: rows)
    }

    // MARK: Bill by bill

    func billsReport(_ r: DateInterval) -> ReportTable {
        let rows = invoices(in: r).map { inv -> ReportRow in
            let c = cost(of: inv)
            return ReportRow(cells: [
                .text(inv.number), .date(inv.date), .text(inv.customerName), .text(inv.poNumber),
                .qty(inv.lines.reduce(0) { $0 + $1.qty }), .money(inv.subtotal), .money(inv.taxAmount),
                .money(inv.grandTotal), .money(c), .money(inv.subtotal - c),
                .percent(ReportTable.margin(profit: inv.subtotal - c, sales: inv.subtotal)),
                .text(quotation(id: inv.quotationId)?.number ?? ""), .text(challan(id: inv.challanId)?.number ?? ""),
            ], open: .invoice(inv))
        }
        return ReportTable(title: "Bill by Bill (Sales Invoices)", columns: [
            ReportColumn(title: "Invoice No.", width: 115, total: .countRows),
            ReportColumn(title: "Date", width: 95, kind: .date),
            ReportColumn(title: "Customer", width: 170),
            ReportColumn(title: "PO No.", width: 95),
            ReportColumn(title: "Qty", width: 60, kind: .qty, total: .sum),
            ReportColumn(title: "Net Sales", kind: .money, total: .sum),
            ReportColumn(title: "Sales Tax", kind: .money, total: .sum),
            ReportColumn(title: "Total", width: 105, kind: .money, total: .sum),
            ReportColumn(title: "Cost", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Profit", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Margin", width: 70, kind: .percent, sensitive: true, total: .ratio(num: 9, den: 5, percent: true)),
            ReportColumn(title: "Quotation", width: 105),
            ReportColumn(title: "DC No.", width: 105),
        ], rows: rows)
    }

    // MARK: Line detail

    func lineReport(_ r: DateInterval) -> ReportTable {
        let rows = saleLines(in: r).map { s in
            ReportRow(cells: [
                .date(s.invoice.date), .text(s.invoice.number), .text(s.invoice.customerName), .text(s.line.code),
                .text(s.line.name), .qty(s.line.qty), .money(s.line.rate), .money(s.amount),
                .money(s.unitCost), .money(s.cost), .money(s.profit),
                .percent(ReportTable.margin(profit: s.profit, sales: s.amount)), .money(s.line.baseRate),
            ], open: .invoice(s.invoice))
        }
        return ReportTable(title: "Line Detail (every item on every bill)", columns: [
            ReportColumn(title: "Date", width: 100, kind: .date, total: .label("TOTAL")),
            ReportColumn(title: "Invoice No.", width: 120, total: .countRows),
            ReportColumn(title: "Customer", width: 160),
            ReportColumn(title: "Item ID", width: 100),
            ReportColumn(title: "Item", width: 170),
            ReportColumn(title: "Qty", width: 70, kind: .qty, total: .sum),
            ReportColumn(title: "Sale Rate", width: 100, kind: .money),
            ReportColumn(title: "Amount", kind: .money, total: .sum),
            ReportColumn(title: "Unit Cost", width: 100, kind: .money, sensitive: true),
            ReportColumn(title: "Cost", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Profit", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Margin", width: 75, kind: .percent, sensitive: true, total: .ratio(num: 10, den: 7, percent: true)),
            ReportColumn(title: "Quoted Rate", width: 100, kind: .money),
        ], rows: rows)
    }

    // MARK: Item by item

    func itemsReport(_ r: DateInterval) -> ReportTable {
        struct Agg { var code = "", name = "", unit = "", qty = 0.0, bills = Set<Int64>(), sales = 0.0, cost = 0.0, itemId: Int64? }
        var map: [String: Agg] = [:]
        for s in saleLines(in: r) {
            let k = key(s.line)
            var a = map[k] ?? Agg(code: s.line.code, name: s.line.name, unit: s.line.unit, itemId: s.line.itemId)
            a.qty += s.line.qty
            a.bills.insert(s.invoice.id)
            a.sales += s.amount
            a.cost += s.cost
            map[k] = a
        }
        var bought: [Int64: Double] = [:]
        for p in purchases(in: r) { for l in p.lines { if let id = l.itemId { bought[id, default: 0] += l.qty } } }

        let rows = map.values.sorted { $0.sales > $1.sales }.map { a -> ReportRow in
            let it = item(id: a.itemId)
            return ReportRow(cells: [
                .text(a.code), .text(a.name), .text(a.unit), .qty(a.qty), .count(a.bills.count), .money(a.sales),
                .money(a.qty == 0 ? 0 : a.sales / a.qty), .money(a.cost), .money(a.sales - a.cost),
                .percent(ReportTable.margin(profit: a.sales - a.cost, sales: a.sales)),
                .qty(a.itemId.flatMap { bought[$0] } ?? 0), it.map { .qty($0.stock) } ?? .text(""),
            ], open: it.map { .item($0) })
        }
        return ReportTable(title: "Item by Item (Sales)", columns: [
            ReportColumn(title: "Item ID", width: 110, total: .countRows),
            ReportColumn(title: "Item", width: 200),
            ReportColumn(title: "Unit", width: 60),
            ReportColumn(title: "Qty Sold", width: 85, kind: .qty, total: .sum),
            ReportColumn(title: "Bills", width: 60, kind: .count),
            ReportColumn(title: "Net Sales", kind: .money, total: .sum),
            ReportColumn(title: "Avg Rate", width: 100, kind: .money, total: .ratio(num: 5, den: 3, percent: false)),
            ReportColumn(title: "Cost", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Profit", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Margin", width: 80, kind: .percent, sensitive: true, total: .ratio(num: 8, den: 5, percent: true)),
            ReportColumn(title: "Purchased", width: 90, kind: .qty, total: .sum),
            ReportColumn(title: "In Stock", width: 85, kind: .qty),
        ], rows: rows)
    }

    // MARK: Customers

    func customersReport(_ r: DateInterval) -> ReportTable {
        struct Agg { var name = "", bills = 0, qty = 0.0, net = 0.0, tax = 0.0, total = 0.0, cost = 0.0, last = Date.distantPast }
        var map: [String: Agg] = [:]
        for inv in invoices(in: r) {
            let k = inv.customerName.lowercased()
            var a = map[k] ?? Agg(name: inv.customerName)
            a.bills += 1
            a.qty += inv.lines.reduce(0) { $0 + $1.qty }
            a.net += inv.subtotal
            a.tax += inv.taxAmount
            a.total += inv.grandTotal
            a.cost += cost(of: inv)
            a.last = max(a.last, inv.date)
            map[k] = a
        }
        var open: [String: Double] = [:]
        for q in quotations where stage(of: q) != .invoiced { open[q.customerName.lowercased(), default: 0] += q.subtotal }

        let rows = map.values.sorted { $0.net > $1.net }.map { a in
            ReportRow(cells: [
                .text(a.name), .count(a.bills), .qty(a.qty), .money(a.net), .money(a.tax), .money(a.total),
                .money(a.net - a.cost), .percent(ReportTable.margin(profit: a.net - a.cost, sales: a.net)),
                .date(a.last), .money(open[a.name.lowercased()] ?? 0),
            ])
        }
        return ReportTable(title: "Customers", columns: [
            ReportColumn(title: "Customer", width: 230, total: .countRows),
            ReportColumn(title: "Bills", width: 60, kind: .count, total: .sum),
            ReportColumn(title: "Qty", width: 75, kind: .qty, total: .sum),
            ReportColumn(title: "Net Sales", kind: .money, total: .sum),
            ReportColumn(title: "Sales Tax", kind: .money, total: .sum),
            ReportColumn(title: "Total", width: 120, kind: .money, total: .sum),
            ReportColumn(title: "Profit", kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Margin", width: 80, kind: .percent, sensitive: true, total: .ratio(num: 6, den: 3, percent: true)),
            ReportColumn(title: "Last Bill", width: 100, kind: .date),
            ReportColumn(title: "Open Quotations", width: 130, kind: .money, total: .sum),
        ], rows: rows)
    }

    // MARK: Orders (quotation → invoice)

    func ordersReport(_ r: DateInterval) -> ReportTable {
        let rows = quotations.filter { r.contains($0.date) }.map { q -> ReportRow in
            let inv = invoice(forQuotation: q.id)
            let st = stage(of: q)
            return ReportRow(cells: [
                .text(q.number), .date(q.date), .text(q.customerName), .text(q.poNumber), .money(q.subtotal),
                .text(st.shortTitle), .text(challan(forQuotation: q.id)?.number ?? ""), .text(inv?.number ?? ""),
                inv.map { .money($0.subtotal) } ?? .text(""),
                inv.map { .money($0.subtotal - q.subtotal) } ?? .text(""),
            ], open: .quotation(q))
        }
        return ReportTable(title: "Orders (Quotation → Challan → Invoice)", columns: [
            ReportColumn(title: "Quotation", width: 120, total: .countRows),
            ReportColumn(title: "Date", width: 100, kind: .date),
            ReportColumn(title: "Customer", width: 200),
            ReportColumn(title: "PO No.", width: 100),
            ReportColumn(title: "Quoted (net)", width: 120, kind: .money, total: .sum),
            ReportColumn(title: "Status", width: 120),
            ReportColumn(title: "Delivery Challan", width: 120),
            ReportColumn(title: "Invoice", width: 120),
            ReportColumn(title: "Invoiced (net)", width: 120, kind: .money, total: .sum),
            ReportColumn(title: "Price Change", width: 110, kind: .money, total: .sum),
        ], rows: rows)
    }

    // MARK: Purchases

    func purchasesReport(_ r: DateInterval) -> ReportTable {
        let rows = purchases(in: r).map { p in
            ReportRow(cells: [
                .date(p.date), .text(p.number), .text(p.supplier), .count(p.lines.count),
                .qty(p.lines.reduce(0) { $0 + $1.qty }), .text(p.lines.map(\.code).joined(separator: ", ")), .money(p.total),
            ], open: .purchase(p))
        }
        return ReportTable(title: "Purchase Invoices (Stock In)", columns: [
            ReportColumn(title: "Date", width: 100, kind: .date, total: .label("TOTAL")),
            ReportColumn(title: "Invoice No.", width: 130, total: .countRows),
            ReportColumn(title: "Supplier", width: 200),
            ReportColumn(title: "Lines", width: 60, kind: .count, total: .sum),
            ReportColumn(title: "Qty", width: 80, kind: .qty, total: .sum),
            ReportColumn(title: "Items", width: 300),
            ReportColumn(title: "Total Cost", width: 120, kind: .money, sensitive: true, total: .sum),
        ], rows: rows)
    }

    // MARK: Stock valuation

    func stockReport(_ r: DateInterval) -> ReportTable {
        var inQty: [Int64: Double] = [:], outQty: [Int64: Double] = [:]
        for p in purchases(in: r) { for l in p.lines { if let id = l.itemId { inQty[id, default: 0] += l.qty } } }
        for c in challans where c.status == .approved && r.contains(c.approvedAt ?? c.date) {
            for l in c.lines { if let id = l.itemId { outQty[id, default: 0] += l.qty } } }
        let rows = items.map { it in
            let s = max(0, it.stock)
            return ReportRow(cells: [
                .text(it.code), .text(it.name), .text(it.unit), .qty(it.stock), .money(it.costPrice), .money(it.salePrice),
                .money(s * it.costPrice), .money(s * it.salePrice), .money(s * (it.salePrice - it.costPrice)),
                .qty(inQty[it.id] ?? 0), .qty(outQty[it.id] ?? 0),
            ], open: .item(it))
        }
        return ReportTable(title: "Stock & Valuation", columns: [
            ReportColumn(title: "Item ID", width: 110, total: .countRows),
            ReportColumn(title: "Item", width: 200),
            ReportColumn(title: "Unit", width: 60),
            ReportColumn(title: "In Stock", width: 85, kind: .qty, total: .sum),
            ReportColumn(title: "Unit Cost", width: 100, kind: .money, sensitive: true),
            ReportColumn(title: "Sale Price", width: 100, kind: .money),
            ReportColumn(title: "Value @ Cost", width: 120, kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "Value @ Sale", width: 120, kind: .money, total: .sum),
            ReportColumn(title: "Potential Profit", width: 125, kind: .money, sensitive: true, total: .sum),
            ReportColumn(title: "In (period)", width: 90, kind: .qty, total: .sum),
            ReportColumn(title: "Out (period)", width: 95, kind: .qty, total: .sum),
        ], rows: rows)
    }
}

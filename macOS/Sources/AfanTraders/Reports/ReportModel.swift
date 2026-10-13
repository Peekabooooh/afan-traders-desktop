import Foundation

/// How a column's total is worked out (over the rows currently shown).
enum ReportTotal {
    case none
    case label(String)
    case sum
    case countRows
    /// sum(column num) / sum(column den), optionally × 100.
    case ratio(num: Int, den: Int, percent: Bool)
}

enum CellKind { case text, money, qty, percent, date, count }

struct ReportColumn {
    let title: String
    var width: CGFloat = 100
    var kind: CellKind = .text
    /// Cost / profit data – masked while cost prices are hidden.
    var sensitive = false
    var total: ReportTotal = .none

    var numeric: Bool { kind != .text && kind != .date }
}

struct ReportCell {
    let text: String
    /// Numeric value for sorting, totals and Excel export.
    let value: Double?

    static func text(_ s: String) -> ReportCell { ReportCell(text: s.isEmpty ? "—" : s, value: nil) }
    static func money(_ v: Double) -> ReportCell { ReportCell(text: Fmt.money(v), value: v.rounded2) }
    static func qty(_ v: Double) -> ReportCell { ReportCell(text: Fmt.qty(v), value: v) }
    static func count(_ v: Int) -> ReportCell { ReportCell(text: "\(v)", value: Double(v)) }
    static func percent(_ v: Double?) -> ReportCell {
        guard let v, v.isFinite else { return ReportCell(text: "—", value: nil) }
        return ReportCell(text: "\(Fmt.qty(v.rounded2))%", value: v.rounded2)
    }
    static func date(_ d: Date) -> ReportCell { ReportCell(text: Fmt.date(d), value: d.timeIntervalSince1970) }
}

struct ReportRow: Identifiable {
    let id = UUID()
    let cells: [ReportCell]
    /// Double-clicking the row opens this document.
    var open: Route.Kind? = nil
}

struct ReportTable {
    var title: String
    var columns: [ReportColumn]
    var rows: [ReportRow]

    static func margin(profit: Double, sales: Double) -> Double? {
        sales == 0 ? nil : profit / sales * 100
    }

    func totals(for rows: [ReportRow]) -> [ReportCell]? {
        guard columns.contains(where: { if case .none = $0.total { return false }; return true }) else { return nil }
        func sum(_ i: Int) -> Double { rows.reduce(0) { $0 + ($1.cells[i].value ?? 0) } }
        return columns.enumerated().map { i, col in
            switch col.total {
            case .none: return ReportCell(text: "", value: nil)
            case .label(let s): return ReportCell(text: s, value: nil)
            case .countRows: return ReportCell(text: "TOTAL · \(rows.count)", value: Double(rows.count))
            case .sum:
                switch col.kind {
                case .qty: return .qty(sum(i))
                case .count: return .count(Int(sum(i)))
                default: return .money(sum(i))
                }
            case .ratio(let n, let d, let pct):
                let den = sum(d)
                guard den != 0 else { return ReportCell(text: "—", value: nil) }
                let v = sum(n) / den * (pct ? 100 : 1)
                return pct ? .percent(v) : .money(v)
            }
        }
    }

    /// Excel-friendly CSV. Hidden cost columns are left out.
    func csv(rows: [ReportRow], includeSensitive: Bool) -> String {
        let keep = columns.indices.filter { includeSensitive || !columns[$0].sensitive }
        func esc(_ s: String) -> String {
            s.contains(",") || s.contains("\"") || s.contains("\n") ? "\"\(s.replacingOccurrences(of: "\"", with: "\"\""))\"" : s
        }
        func field(_ cell: ReportCell, _ col: ReportColumn) -> String {
            if col.numeric, let v = cell.value { return col.kind == .count ? String(Int(v)) : String(format: "%.2f", v) }
            return esc(cell.text == "—" ? "" : cell.text)
        }
        var lines = [keep.map { esc(columns[$0].title) }.joined(separator: ",")]
        for r in rows { lines.append(keep.map { field(r.cells[$0], columns[$0]) }.joined(separator: ",")) }
        if let t = totals(for: rows) { lines.append(keep.map { field(t[$0], columns[$0]) }.joined(separator: ",")) }
        return lines.joined(separator: "\r\n")
    }
}

enum RangePreset: String, CaseIterable, Identifiable {
    case today = "Today"
    case yesterday = "Yesterday"
    case thisWeek = "This Week"
    case thisMonth = "This Month"
    case lastMonth = "Last Month"
    case last30 = "Last 30 Days"
    case thisYear = "This Year"
    case allTime = "All Time"
    case custom = "Custom"

    var id: String { rawValue }

    func interval(from: Date, to: Date) -> DateInterval {
        let cal = Calendar.current
        let now = Date()
        let today = cal.startOfDay(for: now)
        func endOfDay(_ d: Date) -> Date { cal.date(byAdding: DateComponents(day: 1, second: -1), to: cal.startOfDay(for: d))! }
        switch self {
        case .today: return DateInterval(start: today, end: endOfDay(now))
        case .yesterday:
            let y = cal.date(byAdding: .day, value: -1, to: today)!
            return DateInterval(start: y, end: endOfDay(y))
        case .thisWeek:
            return DateInterval(start: cal.dateInterval(of: .weekOfYear, for: now)!.start, end: endOfDay(now))
        case .thisMonth:
            return DateInterval(start: cal.dateInterval(of: .month, for: now)!.start, end: endOfDay(now))
        case .lastMonth:
            let m = cal.dateInterval(of: .month, for: cal.date(byAdding: .month, value: -1, to: now)!)!
            return DateInterval(start: m.start, end: m.end.addingTimeInterval(-1))
        case .last30:
            return DateInterval(start: cal.date(byAdding: .day, value: -29, to: today)!, end: endOfDay(now))
        case .thisYear:
            return DateInterval(start: cal.dateInterval(of: .year, for: now)!.start, end: endOfDay(now))
        case .allTime:
            return DateInterval(start: Date(timeIntervalSince1970: 0), end: endOfDay(now).addingTimeInterval(86400 * 365 * 50))
        case .custom:
            let a = cal.startOfDay(for: min(from, to))
            return DateInterval(start: a, end: endOfDay(max(from, to)))
        }
    }
}

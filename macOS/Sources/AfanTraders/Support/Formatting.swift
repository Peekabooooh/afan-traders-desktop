import Foundation

extension Double {
    var rounded2: Double { (self * 100).rounded() / 100 }
}

extension String {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}

enum Fmt {
    private static let moneyFormatter: NumberFormatter = {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.minimumFractionDigits = 2
        f.maximumFractionDigits = 2
        f.usesGroupingSeparator = true
        return f
    }()

    private static let qtyFormatter: NumberFormatter = {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.minimumFractionDigits = 0
        f.maximumFractionDigits = 3
        f.usesGroupingSeparator = true
        return f
    }()

    private static let spellOut: NumberFormatter = {
        let f = NumberFormatter()
        f.numberStyle = .spellOut
        f.locale = Locale(identifier: "en_US")
        return f
    }()

    private static let docDate: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "dd-MMM-yyyy"
        return f
    }()

    static func money(_ v: Double) -> String { moneyFormatter.string(from: NSNumber(value: v)) ?? "0.00" }

    static func money(_ v: Double, _ currency: String) -> String { "\(currency) \(money(v))" }

    static func qty(_ v: Double) -> String { qtyFormatter.string(from: NSNumber(value: v)) ?? "0" }

    static func date(_ d: Date) -> String { docDate.string(from: d) }

    /// "Rupees Twelve Thousand Five Hundred Only"
    static func words(_ amount: Double, currencyWord: String) -> String {
        let whole = Int(amount.rounded2)
        let fraction = Int(((amount.rounded2 - Double(whole)) * 100).rounded())
        var s = (spellOut.string(from: NSNumber(value: whole)) ?? "").capitalized
        if !currencyWord.isEmpty { s = "\(currencyWord) \(s)" }
        if fraction > 0 {
            let unit = currencyWord.lowercased().contains("rupee") ? "Paisa" : "Cents"
            s += " and \((spellOut.string(from: NSNumber(value: fraction)) ?? "").capitalized) \(unit)"
        }
        return s + " Only"
    }

    /// Plain number for editing (no grouping, no trailing zeros).
    static func editable(_ v: Double) -> String {
        if v == v.rounded() && abs(v) < 1e15 { return String(Int64(v)) }
        var s = String(format: "%.4f", v)
        while s.hasSuffix("0") { s.removeLast() }
        if s.hasSuffix(".") { s.removeLast() }
        return s
    }

    static func parse(_ text: String) -> Double? {
        Double(text.replacingOccurrences(of: ",", with: "").trimmed)
    }
}

import Foundation

/// Everything needed to print a quotation, delivery challan or invoice.
struct PrintableDoc {
    enum Kind { case quotation, challan, invoice }

    var kind: Kind
    var title: String
    var number: String
    var date: Date
    var poNumber: String
    /// Extra key/value lines in the document info box (references, vehicle, …).
    var info: [(String, String)] = []
    var customerName: String
    var customerAddress: String
    var customerPhone: String
    var lines: [DocLine]
    var taxRate: Double = 0
    var notes: String = ""
    var terms: String = ""

    var showPrices: Bool { kind != .challan }
    var subtotal: Double { lines.reduce(0) { $0 + $1.total }.rounded2 }
    var taxAmount: Double { (subtotal * taxRate / 100).rounded2 }
    var grandTotal: Double { (subtotal + taxAmount).rounded2 }
    var totalQty: Double { lines.reduce(0) { $0 + $1.qty } }

    var fileName: String {
        let safe = number.replacingOccurrences(of: "/", with: "-")
        switch kind {
        case .quotation: return "Quotation \(safe).pdf"
        case .challan: return "Delivery Challan \(safe).pdf"
        case .invoice: return "Invoice \(safe).pdf"
        }
    }
}

extension PrintableDoc {
    /// Sample document for checking the letterhead layout.
    static func sample(_ kind: Kind, settings s: CompanySettings) -> PrintableDoc {
        let lines = (1...6).map { i in
            DocLine(code: "ITEM-\(100 + i)", name: "Sample item \(i)", details: i.isMultiple(of: 2) ? "Size / brand / model" : "",
                    unit: "Pcs", qty: Double(i * 2), rate: Double(1250 * i), baseRate: Double(1250 * i))
        }
        let title: String
        switch kind {
        case .quotation: title = "QUOTATION"
        case .challan: title = "DELIVERY CHALLAN"
        case .invoice: title = s.invoiceTitle.isEmpty ? "INVOICE" : s.invoiceTitle.uppercased()
        }
        let prefix = kind == .quotation ? s.quotationPrefix : (kind == .challan ? s.challanPrefix : s.invoicePrefix)
        return PrintableDoc(kind: kind, title: title, number: "\(prefix)-26-00001", date: Date(), poNumber: "PO-1234",
                            info: [("Quotation No.", "\(s.quotationPrefix)-26-00001")],
                            customerName: "Sample Customer (Pvt) Ltd", customerAddress: "Plot 12, Industrial Area\nLahore",
                            customerPhone: "0300-0000000", lines: lines, taxRate: s.defaultTaxRate,
                            notes: "This is a sample to check your letterhead layout.",
                            terms: kind == .invoice ? s.invoiceTerms : s.quotationTerms)
    }
}

struct PreviewDoc: Identifiable {
    let id = UUID()
    let title: String
    let fileName: String
    let data: Data
}

@MainActor
extension AppStore {
    func printable(_ q: Quotation) -> PrintableDoc {
        PrintableDoc(kind: .quotation, title: "QUOTATION", number: q.number, date: q.date, poNumber: q.poNumber,
                     customerName: q.customerName, customerAddress: q.customerAddress,
                     customerPhone: q.customerPhone, lines: q.lines, taxRate: q.taxRate,
                     notes: q.notes, terms: settings.quotationTerms)
    }

    func printable(_ c: Challan) -> PrintableDoc {
        var info: [(String, String)] = []
        if let q = quotation(id: c.quotationId) { info.append(("Quotation No.", q.number)) }
        if !c.vehicleNo.isEmpty { info.append(("Vehicle No.", c.vehicleNo)) }
        if !c.driverName.isEmpty { info.append(("Driver", c.driverName)) }
        return PrintableDoc(kind: .challan, title: "DELIVERY CHALLAN", number: c.number, date: c.date,
                            poNumber: c.poNumber, info: info, customerName: c.customerName,
                            customerAddress: c.customerAddress, customerPhone: c.customerPhone,
                            lines: c.lines, notes: c.notes)
    }

    func printable(_ inv: Invoice) -> PrintableDoc {
        var info: [(String, String)] = []
        if let q = quotation(id: inv.quotationId) { info.append(("Quotation No.", q.number)) }
        if let c = challan(id: inv.challanId) { info.append(("Delivery Challan", c.number)) }
        let title = settings.invoiceTitle.trimmed.isEmpty ? "INVOICE" : settings.invoiceTitle.uppercased()
        return PrintableDoc(kind: .invoice, title: title, number: inv.number, date: inv.date,
                            poNumber: inv.poNumber, info: info, customerName: inv.customerName,
                            customerAddress: inv.customerAddress, customerPhone: inv.customerPhone,
                            lines: inv.lines, taxRate: inv.taxRate, notes: inv.notes,
                            terms: settings.invoiceTerms)
    }

    func preview(_ doc: PrintableDoc, settings override: CompanySettings? = nil) -> PreviewDoc {
        let data = PDFRenderer.render(doc, settings: override ?? settings)
        let kindName: String
        switch doc.kind {
        case .quotation: kindName = "Quotation"
        case .challan: kindName = "Delivery Challan"
        case .invoice: kindName = "Invoice"
        }
        return PreviewDoc(title: "\(kindName) \(doc.number)", fileName: doc.fileName, data: data)
    }
}

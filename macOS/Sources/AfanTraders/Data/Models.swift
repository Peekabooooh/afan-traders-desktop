import Foundation

struct Item: Identifiable, Hashable {
    var id: Int64 = 0
    var code = ""
    var name = ""
    var details = ""
    var unit = "Pcs"
    var imageData: Data? = nil
    var costPrice: Double = 0
    var salePrice: Double = 0
    var stock: Double = 0
    var createdAt = Date()
}

/// A line on a quotation, delivery challan or invoice. Item details are copied
/// so old documents stay correct even if the inventory item changes later.
struct DocLine: Identifiable, Hashable {
    var id = UUID()
    var itemId: Int64? = nil
    var code = ""
    var name = ""
    var details = ""
    var unit = "Pcs"
    var qty: Double = 1
    var rate: Double = 0
    /// The quotation rate this line started from (used on invoices to show margin).
    var baseRate: Double = 0
    /// Unit cost when the line was invoiced (0 = unknown, use the item's current cost).
    var cost: Double = 0

    var total: Double { (qty * rate).rounded2 }
}

protocol PricedDocument {
    var lines: [DocLine] { get }
    var taxRate: Double { get }
}

extension PricedDocument {
    var subtotal: Double { lines.reduce(0) { $0 + $1.total }.rounded2 }
    var taxAmount: Double { (subtotal * taxRate / 100).rounded2 }
    var grandTotal: Double { (subtotal + taxAmount).rounded2 }
}

struct Quotation: Identifiable, Hashable, PricedDocument {
    var id: Int64 = 0
    var number = ""
    var poNumber = ""
    var customerName = ""
    var customerAddress = ""
    var customerPhone = ""
    var date = Date()
    var taxRate: Double = 18
    var notes = ""
    var lines: [DocLine] = []
}

enum ChallanStatus: String {
    case pending, approved
}

struct Challan: Identifiable, Hashable {
    var id: Int64 = 0
    var number = ""
    var quotationId: Int64 = 0
    var poNumber = ""
    var customerName = ""
    var customerAddress = ""
    var customerPhone = ""
    var date = Date()
    var status: ChallanStatus = .pending
    var approvedAt: Date? = nil
    var vehicleNo = ""
    var driverName = ""
    var notes = ""
    var lines: [DocLine] = []

    var totalQty: Double { lines.reduce(0) { $0 + $1.qty } }
}

struct Invoice: Identifiable, Hashable, PricedDocument {
    var id: Int64 = 0
    var number = ""
    var quotationId: Int64 = 0
    var challanId: Int64 = 0
    var poNumber = ""
    var customerName = ""
    var customerAddress = ""
    var customerPhone = ""
    var date = Date()
    var taxRate: Double = 18
    var notes = ""
    var lines: [DocLine] = []
}

struct PurchaseLine: Identifiable, Hashable {
    var id = UUID()
    var itemId: Int64? = nil
    var code = ""
    var name = ""
    var details = ""
    var unit = "Pcs"
    var qty: Double = 1
    var cost: Double = 0
    var salePrice: Double = 0
    /// Optional picture for a new item (saved on the item, not on the purchase).
    var imageData: Data? = nil

    var total: Double { (qty * cost).rounded2 }
    var isBlank: Bool { code.trimmed.isEmpty && name.trimmed.isEmpty }
}

struct Purchase: Identifiable, Hashable {
    var id: Int64 = 0
    var number = ""
    var supplier = ""
    var date = Date()
    var notes = ""
    var lines: [PurchaseLine] = []

    var total: Double { lines.reduce(0) { $0 + $1.total }.rounded2 }
}

struct CompanySettings: Equatable {
    var companyName = "AFAN TRADERS"
    var tagline = "General Order Suppliers"
    var address = ""
    var phone = ""
    var email = ""
    var ntn = ""
    var strn = ""
    var logo: Data? = nil

    var defaultTaxRate: Double = 18
    var currency = "Rs."
    var currencyWord = "Rupees"

    var quotationPrefix = "QT"
    var challanPrefix = "DC"
    var invoicePrefix = "INV"
    var invoiceTitle = "SALES TAX INVOICE"

    var quotationTerms = "Prices are valid for 15 days.\nDelivery within 7 working days after PO."
    var invoiceTerms = "Payment due within 30 days.\nCheques in favour of AFAN TRADERS."

    /// User's own letterhead (PDF or image). When set, documents print on it.
    var letterhead: Data? = nil
    var letterheadTop: Double = 150
    var letterheadBottom: Double = 90
}

/// Where a quotation is in the Quotation → Delivery Challan → Invoice flow.
enum OrderStage: String, CaseIterable, Identifiable {
    case needsChallan = "Needs Delivery Challan"
    case awaitingApproval = "Challan Pending Approval"
    case readyToInvoice = "Ready to Invoice"
    case invoiced = "Invoiced"

    var id: String { rawValue }
}

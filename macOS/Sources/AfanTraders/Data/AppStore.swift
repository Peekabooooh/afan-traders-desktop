import Foundation
import SwiftUI
import AppKit

/// A sheet the app is showing (editor or PDF preview).
struct Route: Identifiable {
    enum Kind {
        case item(Item)
        case purchase(Purchase)
        case quotation(Quotation)
        case challan(Challan)
        case invoice(Invoice)
        case preview(PreviewDoc)
    }
    let id = UUID()
    let kind: Kind
}

@MainActor
final class AppStore: ObservableObject {
    @Published private(set) var items: [Item] = []
    @Published private(set) var purchases: [Purchase] = []
    @Published private(set) var quotations: [Quotation] = []
    @Published private(set) var challans: [Challan] = []
    @Published private(set) var invoices: [Invoice] = []
    @Published private(set) var settings = CompanySettings()

    /// Cost prices are hidden every time the app starts.
    @Published var showCost = false
    @Published var route: Route?
    @Published var section: AppSection = .dashboard
    @Published var quotationFilter: OrderStage? = nil
    @Published var alertMessage: String?

    let dataFolder: URL
    let db: SQLiteDB
    /// Cloud sync (Supabase). Inactive until connected in Settings.
    private(set) var sync: SyncEngine!
    /// Office network sync (computers on the same network). Inactive until turned on in Settings.
    private(set) var lan: LANSync!

    init(folder: URL? = nil) {
        let base = folder ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AFAN Traders", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        dataFolder = base
        do {
            db = try SQLiteDB(path: base.appendingPathComponent("afan-traders.sqlite").path)
            try Schema.migrate(db)
        } catch {
            fatalError("Could not open the database: \(error.localizedDescription)")
        }
        try? db.exec(Schema.recomputeStockSQL)
        compressOldPictures()
        reloadAll()
        sync = SyncEngine(store: self)
        lan = LANSync(store: self)
    }

    // MARK: - Change tracking (for cloud sync)

    /// Marks a row as changed so it is uploaded on the next sync. Gives it a uid if it has none.
    func touch(_ table: String, _ id: Int64) throws {
        try db.exec("UPDATE lan_clock SET n = n + 1;")
        try db.run("""
            UPDATE \(table) SET uid = COALESCE(uid, ?), updated_at = ?, dirty = 1, lan_seq = (SELECT n FROM lan_clock)
            WHERE id = ?
            """, [UUID().uuidString.lowercased(), Date().timeIntervalSince1970, id])
    }

    /// Gives a row the next change number (so office computers pick it up).
    func bumpLAN(_ table: String, uid: String, dirty: Bool) throws {
        try db.exec("UPDATE lan_clock SET n = n + 1;")
        try db.run("UPDATE \(table) SET lan_seq = (SELECT n FROM lan_clock), dirty = CASE WHEN ? THEN 1 ELSE dirty END WHERE uid = ?",
                   [dirty, uid])
    }

    func nextLANSeq() throws -> Int64 {
        try db.exec("UPDATE lan_clock SET n = n + 1;")
        return try db.query("SELECT n FROM lan_clock").first?.int("n") ?? 0
    }

    /// Remembers a deleted row so the deletion reaches the other computers.
    func tombstone(_ table: String, _ id: Int64) throws {
        let seq = try nextLANSeq()
        try db.run("""
            INSERT INTO tombstones (tbl, uid, deleted_at, pushed, lan_seq) SELECT ?, uid, ?, 0, ? FROM \(table)
            WHERE id = ? AND uid IS NOT NULL
            """, [table, Date().timeIntervalSince1970, seq, id])
    }

    func recomputeStock() throws { try db.exec(Schema.recomputeStockSQL) }

    /// Called after every local change.
    func changed() {
        sync?.localChanged()
        lan?.localChanged()
    }

    /// One time: re-save item pictures stored as large PNGs as small JPEGs.
    private func compressOldPictures() {
        guard syncValue("pictures_compressed") == nil else { return }
        do {
            for r in try db.query("SELECT id, image FROM items WHERE image IS NOT NULL AND length(image) > 120000") {
                guard let data = r.data("image"), let img = NSImage(data: data),
                      let small = ImageUtil.compressed(img), small.count < data.count else { continue }
                try db.run("UPDATE items SET image = ? WHERE id = ?", [small, r.int("id")])
                try touch("items", r.int("id"))
            }
            try setSyncValue("pictures_compressed", "1")
        } catch {
            report(error)
        }
    }

    func open(_ kind: Route.Kind) { route = Route(kind: kind) }

    func report(_ error: Error) { alertMessage = error.localizedDescription }

    // MARK: - Lookups

    func item(id: Int64?) -> Item? {
        guard let id else { return nil }
        return items.first { $0.id == id }
    }

    func item(code: String) -> Item? {
        let c = code.trimmed
        return items.first { $0.code.caseInsensitiveCompare(c) == .orderedSame }
    }

    func quotation(id: Int64) -> Quotation? { quotations.first { $0.id == id } }
    func challan(id: Int64) -> Challan? { challans.first { $0.id == id } }
    func challan(forQuotation id: Int64) -> Challan? { id == 0 ? nil : challans.first { $0.quotationId == id } }
    func invoice(forQuotation id: Int64) -> Invoice? { id == 0 ? nil : invoices.first { $0.quotationId == id } }
    func invoice(forChallan id: Int64) -> Invoice? { id == 0 ? nil : invoices.first { $0.challanId == id } }

    func stage(of q: Quotation) -> OrderStage {
        guard let c = challan(forQuotation: q.id) else { return .needsChallan }
        if invoice(forQuotation: q.id) != nil { return .invoiced }
        return c.status == .approved ? .readyToInvoice : .awaitingApproval
    }

    /// Previously used customers, newest first, for quick fill.
    var knownCustomers: [(name: String, address: String, phone: String)] {
        var seen = Set<String>()
        var result: [(String, String, String)] = []
        for q in quotations where !q.customerName.isEmpty {
            let key = q.customerName.lowercased()
            if seen.insert(key).inserted { result.append((q.customerName, q.customerAddress, q.customerPhone)) }
        }
        return result
    }

    // MARK: - Numbers

    /// Random, unique document number like "QT-26-48213". Users can edit it.
    func newNumber(prefix: String, existing: [String]) -> String {
        let taken = Set(existing.map { $0.lowercased() })
        let yy = String(Calendar.current.component(.year, from: Date())).suffix(2)
        let p = prefix.trimmed.isEmpty ? "" : "\(prefix.trimmed)-"
        while true {
            let n = "\(p)\(yy)-\(Int.random(in: 10000...99999))"
            if !taken.contains(n.lowercased()) { return n }
        }
    }

    func newQuotationNumber() -> String { newNumber(prefix: settings.quotationPrefix, existing: quotations.map(\.number)) }
    func newChallanNumber() -> String { newNumber(prefix: settings.challanPrefix, existing: challans.map(\.number)) }
    func newInvoiceNumber() -> String { newNumber(prefix: settings.invoicePrefix, existing: invoices.map(\.number)) }

    // MARK: - Loading

    func reloadAll() {
        do {
            settings = try loadSettings()
            items = try loadItems()
            purchases = try loadPurchases()
            quotations = try loadQuotations()
            challans = try loadChallans()
            invoices = try loadInvoices()
        } catch {
            report(error)
        }
    }

    private func loadItems() throws -> [Item] {
        try db.query("SELECT * FROM items ORDER BY code COLLATE NOCASE").map { r in
            Item(id: r.int("id"), code: r.string("code"), name: r.string("name"), details: r.string("details"),
                 unit: r.string("unit"), imageData: r.data("image"), costPrice: r.double("cost_price"),
                 salePrice: r.double("sale_price"), stock: r.double("stock"), createdAt: r.date("created_at"))
        }
    }

    private func loadLines(_ type: String) throws -> [Int64: [DocLine]] {
        var map: [Int64: [DocLine]] = [:]
        for r in try db.query("SELECT * FROM doc_lines WHERE doc_type = ? ORDER BY doc_id, position", [type]) {
            let line = DocLine(itemId: r.optInt("item_id"), code: r.string("code"), name: r.string("name"),
                               details: r.string("details"), unit: r.string("unit"), qty: r.double("qty"),
                               rate: r.double("rate"), baseRate: r.double("base_rate"), cost: r.double("cost"))
            map[r.int("doc_id"), default: []].append(line)
        }
        return map
    }

    private func loadPurchases() throws -> [Purchase] {
        var lines: [Int64: [PurchaseLine]] = [:]
        for r in try db.query("SELECT * FROM purchase_lines ORDER BY purchase_id, position") {
            let l = PurchaseLine(itemId: r.optInt("item_id"), code: r.string("code"), name: r.string("name"),
                                 details: r.string("details"), unit: r.string("unit"), qty: r.double("qty"),
                                 cost: r.double("cost"), salePrice: r.double("sale_price"))
            lines[r.int("purchase_id"), default: []].append(l)
        }
        return try db.query("SELECT * FROM purchases ORDER BY date DESC, id DESC").map { r in
            Purchase(id: r.int("id"), number: r.string("number"), supplier: r.string("supplier"),
                     date: r.date("date"), notes: r.string("notes"), lines: lines[r.int("id")] ?? [])
        }
    }

    private func loadQuotations() throws -> [Quotation] {
        let lines = try loadLines("Q")
        return try db.query("SELECT * FROM quotations ORDER BY date DESC, id DESC").map { r in
            Quotation(id: r.int("id"), number: r.string("number"), poNumber: r.string("po_number"),
                      customerName: r.string("customer_name"), customerAddress: r.string("customer_address"),
                      customerPhone: r.string("customer_phone"), date: r.date("date"),
                      taxRate: r.double("tax_rate"), notes: r.string("notes"), lines: lines[r.int("id")] ?? [])
        }
    }

    private func loadChallans() throws -> [Challan] {
        let lines = try loadLines("D")
        return try db.query("SELECT * FROM challans ORDER BY date DESC, id DESC").map { r in
            Challan(id: r.int("id"), number: r.string("number"), quotationId: r.int("quotation_id"),
                    poNumber: r.string("po_number"), customerName: r.string("customer_name"),
                    customerAddress: r.string("customer_address"), customerPhone: r.string("customer_phone"),
                    date: r.date("date"), status: ChallanStatus(rawValue: r.string("status")) ?? .pending,
                    approvedAt: r.optDate("approved_at"), vehicleNo: r.string("vehicle_no"),
                    driverName: r.string("driver_name"), notes: r.string("notes"), lines: lines[r.int("id")] ?? [])
        }
    }

    private func loadInvoices() throws -> [Invoice] {
        let lines = try loadLines("I")
        return try db.query("SELECT * FROM invoices ORDER BY date DESC, id DESC").map { r in
            Invoice(id: r.int("id"), number: r.string("number"), quotationId: r.int("quotation_id"),
                    challanId: r.int("challan_id"), poNumber: r.string("po_number"),
                    customerName: r.string("customer_name"), customerAddress: r.string("customer_address"),
                    customerPhone: r.string("customer_phone"), date: r.date("date"),
                    taxRate: r.double("tax_rate"), notes: r.string("notes"), lines: lines[r.int("id")] ?? [])
        }
    }

    private func replaceLines(_ type: String, docId: Int64, _ lines: [DocLine]) throws {
        try db.run("DELETE FROM doc_lines WHERE doc_type = ? AND doc_id = ?", [type, docId])
        for (pos, l) in lines.enumerated() {
            try db.run("""
                INSERT INTO doc_lines (doc_type, doc_id, item_id, code, name, details, unit, qty, rate, base_rate, cost, position)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                """, [type, docId, l.itemId, l.code, l.name, l.details, l.unit, l.qty, l.rate, l.baseRate, l.cost, pos])
        }
    }

    // MARK: - Items

    @discardableResult
    func saveItem(_ input: Item) throws -> Item {
        var item = input
        item.code = item.code.trimmed
        item.name = item.name.trimmed
        guard !item.code.isEmpty else { throw AppError("Item ID is required.") }
        guard !item.name.isEmpty else { throw AppError("Item name is required.") }
        if items.contains(where: { $0.id != item.id && $0.code.caseInsensitiveCompare(item.code) == .orderedSame }) {
            throw AppError("Item ID \"\(item.code)\" is already used by another item.")
        }
        try db.transaction {
            if item.id == 0 {
                item.id = try db.run("""
                    INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, [item.code, item.name, item.details, item.unit, item.imageData, item.costPrice,
                          item.salePrice, item.stock, item.stock, Date()])
            } else {
                // A typed-in stock count becomes the manual adjustment on top of purchases and deliveries.
                let moved = try stockMovement(item.id)
                try db.run("""
                    UPDATE items SET code=?, name=?, details=?, unit=?, image=?, cost_price=?, sale_price=?, stock_adjust=?
                    WHERE id=?
                    """, [item.code, item.name, item.details, item.unit, item.imageData, item.costPrice,
                          item.salePrice, item.stock - moved, item.id])
            }
            try touch("items", item.id)
            try recomputeStock()
        }
        items = try loadItems()
        changed()
        return item
    }

    func deleteItem(_ item: Item) throws {
        try db.transaction {
            try tombstone("items", item.id)
            try db.run("DELETE FROM items WHERE id = ?", [item.id])
        }
        items = try loadItems()
        changed()
    }

    /// Purchased minus delivered (approved challans) for one item.
    private func stockMovement(_ itemId: Int64) throws -> Double {
        let r = try db.query("""
            SELECT COALESCE((SELECT SUM(qty) FROM purchase_lines WHERE item_id = ?), 0)
                 - COALESCE((SELECT SUM(dl.qty) FROM doc_lines dl JOIN challans c ON c.id = dl.doc_id
                             WHERE dl.doc_type = 'D' AND c.status = 'approved' AND dl.item_id = ?), 0) AS m
            """, [itemId, itemId])
        return r.first?.double("m") ?? 0
    }

    // MARK: - Purchases (stock in)

    func draftPurchase() -> Purchase { Purchase() }

    func savePurchase(_ input: Purchase) throws {
        var p = input
        p.number = p.number.trimmed
        guard !p.number.isEmpty else {
            throw AppError("Enter the purchase invoice number so this stock is linked to it.")
        }
        let lines = p.lines.filter { !$0.isBlank }
        guard !lines.isEmpty else { throw AppError("Add at least one item.") }
        for (i, l) in lines.enumerated() {
            if l.code.trimmed.isEmpty { throw AppError("Row \(i + 1) (\(l.name)) needs an Item ID.") }
            if l.qty <= 0 { throw AppError("Quantity for \(l.code) must be more than zero.") }
            if item(id: l.itemId) == nil && item(code: l.code) == nil && l.name.trimmed.isEmpty {
                throw AppError("Enter a name for the new item \(l.code).")
            }
        }
        try db.transaction {
            if p.id != 0 {
                try db.run("DELETE FROM purchase_lines WHERE purchase_id = ?", [p.id])
                try db.run("UPDATE purchases SET number=?, supplier=?, date=?, notes=? WHERE id=?",
                           [p.number.trimmed, p.supplier.trimmed, p.date, p.notes, p.id])
            } else {
                p.id = try db.run("INSERT INTO purchases (number, supplier, date, notes) VALUES (?,?,?,?)",
                                  [p.number.trimmed, p.supplier.trimmed, p.date, p.notes])
            }
            for (pos, l) in lines.enumerated() {
                let itemId = try addStock(for: l)
                try db.run("""
                    INSERT INTO purchase_lines (purchase_id, item_id, code, name, details, unit, qty, cost, sale_price, position)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, [p.id, itemId, l.code.trimmed, l.name.trimmed, l.details, l.unit, l.qty, l.cost, l.salePrice, pos])
                try touch("items", itemId)
            }
            try touch("purchases", p.id)
            try recomputeStock()
        }
        items = try loadItems()
        purchases = try loadPurchases()
        changed()
    }

    /// Adds the purchased quantity to an existing item, or creates the item.
    private func addStock(for l: PurchaseLine) throws -> Int64 {
        var existing: Int64?
        if let id = l.itemId, try !db.query("SELECT id FROM items WHERE id = ?", [id]).isEmpty {
            existing = id
        } else {
            existing = try db.query("SELECT id FROM items WHERE code = ? COLLATE NOCASE", [l.code.trimmed]).first?.int("id")
        }
        if let id = existing {
            try db.run("""
                UPDATE items SET cost_price = ?,
                    sale_price = CASE WHEN ? > 0 THEN ? ELSE sale_price END
                WHERE id = ?
                """, [l.cost, l.salePrice, l.salePrice, id])
            if let image = l.imageData {
                try db.run("UPDATE items SET image = ? WHERE id = ?", [image, id])
            }
            return id
        }
        return try db.run("""
            INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at)
            VALUES (?,?,?,?,?,?,?,0,0,?)
            """, [l.code.trimmed, l.name.trimmed, l.details, l.unit.trimmed.isEmpty ? "Pcs" : l.unit.trimmed,
                  l.imageData, l.cost, l.salePrice, Date()])
    }

    struct StockMove: Identifiable {
        let id = UUID()
        let date: Date
        let reference: String
        let party: String
        let qty: Double
        let cost: Double?
    }

    /// Stock in (purchase invoices) and stock out (approved delivery challans) for one item.
    func stockHistory(of item: Item) -> [StockMove] {
        var moves: [StockMove] = []
        for p in purchases {
            for l in p.lines where l.itemId == item.id {
                moves.append(StockMove(date: p.date, reference: "Purchase Inv. \(p.number)", party: p.supplier,
                                       qty: l.qty, cost: l.cost))
            }
        }
        for c in challans where c.status == .approved {
            for l in c.lines where l.itemId == item.id {
                moves.append(StockMove(date: c.approvedAt ?? c.date, reference: "Delivery Challan \(c.number)",
                                       party: c.customerName, qty: -l.qty, cost: nil))
            }
        }
        return moves.sorted { $0.date > $1.date }
    }

    func lastPurchase(of item: Item) -> Purchase? {
        purchases.first { $0.lines.contains { $0.itemId == item.id } }
    }

    func deletePurchase(_ p: Purchase) throws {
        try db.transaction {
            try tombstone("purchases", p.id)
            try db.run("DELETE FROM purchase_lines WHERE purchase_id = ?", [p.id])
            try db.run("DELETE FROM purchases WHERE id = ?", [p.id])
            try recomputeStock()
        }
        items = try loadItems()
        purchases = try loadPurchases()
        changed()
    }

    // MARK: - Quotations

    func draftQuotation() -> Quotation {
        Quotation(number: newQuotationNumber(), taxRate: settings.defaultTaxRate)
    }

    @discardableResult
    func saveQuotation(_ input: Quotation) throws -> Quotation {
        var q = input
        q.number = q.number.trimmed
        q.poNumber = q.poNumber.trimmed
        q.customerName = q.customerName.trimmed
        guard !q.number.isEmpty else { throw AppError("Quotation number is required.") }
        guard !q.customerName.isEmpty else { throw AppError("Customer name is required.") }
        guard !q.lines.isEmpty else { throw AppError("Add at least one item to the quotation.") }
        if quotations.contains(where: { $0.id != q.id && $0.number.caseInsensitiveCompare(q.number) == .orderedSame }) {
            throw AppError("Quotation number \(q.number) is already used.")
        }
        let locked = challan(forQuotation: q.id) != nil
        try db.transaction {
            let params: [Any?] = [q.number, q.poNumber, q.customerName, q.customerAddress, q.customerPhone,
                                  q.date, q.taxRate, q.notes]
            if q.id == 0 {
                q.id = try db.run("""
                    INSERT INTO quotations (number, po_number, customer_name, customer_address, customer_phone, date, tax_rate, notes)
                    VALUES (?,?,?,?,?,?,?,?)
                    """, params)
            } else {
                try db.run("""
                    UPDATE quotations SET number=?, po_number=?, customer_name=?, customer_address=?,
                        customer_phone=?, date=?, tax_rate=?, notes=? WHERE id=?
                    """, params + [q.id])
                // The PO number links all three documents.
                for r in try db.query("SELECT id FROM challans WHERE quotation_id=? AND po_number<>?", [q.id, q.poNumber]) {
                    try db.run("UPDATE challans SET po_number=? WHERE id=?", [q.poNumber, r.int("id")])
                    try touch("challans", r.int("id"))
                }
                for r in try db.query("SELECT id FROM invoices WHERE quotation_id=? AND po_number<>?", [q.id, q.poNumber]) {
                    try db.run("UPDATE invoices SET po_number=? WHERE id=?", [q.poNumber, r.int("id")])
                    try touch("invoices", r.int("id"))
                }
            }
            if !locked { try replaceLines("Q", docId: q.id, q.lines) }
            try touch("quotations", q.id)
        }
        quotations = try loadQuotations()
        challans = try loadChallans()
        invoices = try loadInvoices()
        changed()
        return q
    }

    func deleteQuotation(_ q: Quotation) throws {
        guard challan(forQuotation: q.id) == nil else {
            throw AppError("This quotation has a delivery challan. Delete the challan first.")
        }
        try db.transaction {
            try tombstone("quotations", q.id)
            try db.run("DELETE FROM doc_lines WHERE doc_type='Q' AND doc_id=?", [q.id])
            try db.run("DELETE FROM quotations WHERE id=?", [q.id])
        }
        quotations = try loadQuotations()
        changed()
    }

    // MARK: - Delivery challans

    func draftChallan(from q: Quotation) -> Challan {
        Challan(number: newChallanNumber(), quotationId: q.id, poNumber: q.poNumber,
                customerName: q.customerName, customerAddress: q.customerAddress,
                customerPhone: q.customerPhone, lines: q.lines)
    }

    @discardableResult
    func saveChallan(_ input: Challan) throws -> Challan {
        var c = input
        c.number = c.number.trimmed
        guard !c.number.isEmpty else { throw AppError("Delivery challan number is required.") }
        if challans.contains(where: { $0.id != c.id && $0.number.caseInsensitiveCompare(c.number) == .orderedSame }) {
            throw AppError("Delivery challan number \(c.number) is already used.")
        }
        try db.transaction {
            if c.id == 0 {
                guard challan(forQuotation: c.quotationId) == nil else {
                    throw AppError("This quotation already has a delivery challan.")
                }
                c.id = try db.run("""
                    INSERT INTO challans (number, quotation_id, po_number, customer_name, customer_address,
                        customer_phone, date, status, vehicle_no, driver_name, notes)
                    VALUES (?,?,?,?,?,?,?,'pending',?,?,?)
                    """, [c.number, c.quotationId, c.poNumber, c.customerName, c.customerAddress,
                          c.customerPhone, c.date, c.vehicleNo, c.driverName, c.notes])
                try replaceLines("D", docId: c.id, c.lines)
                c.status = .pending
            } else {
                try db.run("""
                    UPDATE challans SET number=?, customer_name=?, customer_address=?, customer_phone=?,
                        date=?, vehicle_no=?, driver_name=?, notes=? WHERE id=?
                    """, [c.number, c.customerName, c.customerAddress, c.customerPhone, c.date,
                          c.vehicleNo, c.driverName, c.notes, c.id])
            }
            try touch("challans", c.id)
        }
        challans = try loadChallans()
        changed()
        return challan(id: c.id) ?? c
    }

    /// Items on the challan that don't have enough stock.
    func shortages(for c: Challan) -> [String] {
        var need: [Int64: Double] = [:]
        for l in c.lines { if let id = l.itemId { need[id, default: 0] += l.qty } }
        return need.compactMap { id, qty in
            guard let it = item(id: id), it.stock < qty else { return nil }
            return "\(it.code) – \(it.name): need \(Fmt.qty(qty)), in stock \(Fmt.qty(it.stock))"
        }.sorted()
    }

    /// Approving a challan means the goods leave the warehouse, so stock goes down.
    func setApproved(_ c: Challan, _ approved: Bool) throws {
        guard let current = challan(id: c.id) else { throw AppError("Save the delivery challan first.") }
        let target: ChallanStatus = approved ? .approved : .pending
        guard current.status != target else { return }
        if !approved, invoice(forChallan: c.id) != nil {
            throw AppError("An invoice exists for this challan. Delete the invoice first.")
        }
        try db.transaction {
            try db.run("UPDATE challans SET status=?, approved_at=? WHERE id=?",
                       [target.rawValue, approved ? Date() : nil, c.id])
            try touch("challans", c.id)
            try recomputeStock()
        }
        items = try loadItems()
        challans = try loadChallans()
        changed()
    }

    func deleteChallan(_ c: Challan) throws {
        guard invoice(forChallan: c.id) == nil else {
            throw AppError("An invoice exists for this challan. Delete the invoice first.")
        }
        try db.transaction {
            try tombstone("challans", c.id)
            try db.run("DELETE FROM doc_lines WHERE doc_type='D' AND doc_id=?", [c.id])
            try db.run("DELETE FROM challans WHERE id=?", [c.id])
            try recomputeStock()   // an approved challan's quantities go back into stock
        }
        items = try loadItems()
        challans = try loadChallans()
        changed()
    }

    // MARK: - Invoices

    func draftInvoice(from c: Challan) throws -> Invoice {
        guard c.status == .approved else { throw AppError("Approve the delivery challan before creating the invoice.") }
        if let existing = invoice(forChallan: c.id) { return existing }
        guard let q = quotation(id: c.quotationId) else { throw AppError("The quotation for this challan was not found.") }
        let lines = q.lines.map { l -> DocLine in
            var line = l
            line.id = UUID()
            line.baseRate = l.rate
            line.cost = item(id: l.itemId)?.costPrice ?? 0
            return line
        }
        return Invoice(number: newInvoiceNumber(), quotationId: q.id, challanId: c.id, poNumber: q.poNumber,
                       customerName: q.customerName, customerAddress: q.customerAddress,
                       customerPhone: q.customerPhone, taxRate: q.taxRate, lines: lines)
    }

    @discardableResult
    func saveInvoice(_ input: Invoice) throws -> Invoice {
        var inv = input
        inv.number = inv.number.trimmed
        guard !inv.number.isEmpty else { throw AppError("Invoice number is required.") }
        if invoices.contains(where: { $0.id != inv.id && $0.number.caseInsensitiveCompare(inv.number) == .orderedSame }) {
            throw AppError("Invoice number \(inv.number) is already used.")
        }
        try db.transaction {
            if inv.id == 0 {
                guard invoice(forChallan: inv.challanId) == nil else {
                    throw AppError("An invoice already exists for this delivery challan.")
                }
                inv.id = try db.run("""
                    INSERT INTO invoices (number, quotation_id, challan_id, po_number, customer_name,
                        customer_address, customer_phone, date, tax_rate, notes)
                    VALUES (?,?,?,?,?,?,?,?,?,?)
                    """, [inv.number, inv.quotationId, inv.challanId, inv.poNumber, inv.customerName,
                          inv.customerAddress, inv.customerPhone, inv.date, inv.taxRate, inv.notes])
            } else {
                try db.run("""
                    UPDATE invoices SET number=?, customer_name=?, customer_address=?, customer_phone=?,
                        date=?, tax_rate=?, notes=? WHERE id=?
                    """, [inv.number, inv.customerName, inv.customerAddress, inv.customerPhone, inv.date,
                          inv.taxRate, inv.notes, inv.id])
            }
            try replaceLines("I", docId: inv.id, inv.lines)
            try touch("invoices", inv.id)
        }
        invoices = try loadInvoices()
        changed()
        return inv
    }

    func deleteInvoice(_ inv: Invoice) throws {
        try db.transaction {
            try tombstone("invoices", inv.id)
            try db.run("DELETE FROM doc_lines WHERE doc_type='I' AND doc_id=?", [inv.id])
            try db.run("DELETE FROM invoices WHERE id=?", [inv.id])
        }
        invoices = try loadInvoices()
        changed()
    }

    // MARK: - Settings

    func loadSettings() throws -> CompanySettings {
        var kv: [String: Row] = [:]
        for r in try db.query("SELECT * FROM settings") { kv[r.string("key")] = r }
        var s = CompanySettings()
        func text(_ key: String, _ fallback: String) -> String { kv[key].map { $0.string("value") } ?? fallback }
        func number(_ key: String, _ fallback: Double) -> Double { Double(text(key, "")) ?? fallback }

        s.companyName = text("companyName", s.companyName)
        s.tagline = text("tagline", s.tagline)
        s.address = text("address", s.address)
        s.phone = text("phone", s.phone)
        s.email = text("email", s.email)
        s.ntn = text("ntn", s.ntn)
        s.strn = text("strn", s.strn)
        s.defaultTaxRate = number("defaultTaxRate", s.defaultTaxRate)
        s.currency = text("currency", s.currency)
        s.currencyWord = text("currencyWord", s.currencyWord)
        s.quotationPrefix = text("quotationPrefix", s.quotationPrefix)
        s.challanPrefix = text("challanPrefix", s.challanPrefix)
        s.invoicePrefix = text("invoicePrefix", s.invoicePrefix)
        s.invoiceTitle = text("invoiceTitle", s.invoiceTitle)
        s.quotationTerms = text("quotationTerms", s.quotationTerms)
        s.invoiceTerms = text("invoiceTerms", s.invoiceTerms)
        s.letterheadTop = number("letterheadTop", s.letterheadTop)
        s.letterheadBottom = number("letterheadBottom", s.letterheadBottom)
        s.logo = kv["logo"]?.data("data")
        s.letterhead = kv["letterhead"]?.data("data")
        return s
    }

    func saveSettings(_ s: CompanySettings) throws {
        let values: [(String, String)] = [
            ("companyName", s.companyName), ("tagline", s.tagline), ("address", s.address),
            ("phone", s.phone), ("email", s.email), ("ntn", s.ntn), ("strn", s.strn),
            ("defaultTaxRate", String(s.defaultTaxRate)), ("currency", s.currency),
            ("currencyWord", s.currencyWord), ("quotationPrefix", s.quotationPrefix),
            ("challanPrefix", s.challanPrefix), ("invoicePrefix", s.invoicePrefix),
            ("invoiceTitle", s.invoiceTitle), ("quotationTerms", s.quotationTerms),
            ("invoiceTerms", s.invoiceTerms), ("letterheadTop", String(s.letterheadTop)),
            ("letterheadBottom", String(s.letterheadBottom)),
        ]
        try db.transaction {
            for (k, v) in values {
                try db.run("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", [k, v])
            }
            try db.run("INSERT OR REPLACE INTO settings (key, data) VALUES ('logo', ?)", [s.logo])
            try db.run("INSERT OR REPLACE INTO settings (key, data) VALUES ('letterhead', ?)", [s.letterhead])
            try setSyncValue("settings_updated", String(Date().timeIntervalSince1970))
            try setSyncValue("settings_dirty", "1")
            try setSyncValue("settings_lan_seq", String(try nextLANSeq()))
        }
        settings = try loadSettings()
        changed()
    }

    /// Settings values as plain text (used by sync). Logo and letterhead are separate.
    func settingsValues() throws -> [String: String] {
        var out: [String: String] = [:]
        for r in try db.query("SELECT key, value FROM settings WHERE value IS NOT NULL") { out[r.string("key")] = r.string("value") }
        return out
    }

    func syncValue(_ key: String) -> String? {
        (try? db.query("SELECT value FROM sync_state WHERE key = ?", [key]))?.first.map { $0.string("value") }
    }

    func setSyncValue(_ key: String, _ value: String?) throws {
        if let value {
            try db.run("INSERT OR REPLACE INTO sync_state (key, value) VALUES (?, ?)", [key, value])
        } else {
            try db.run("DELETE FROM sync_state WHERE key = ?", [key])
        }
    }

    /// Reloads everything after remote changes were applied.
    func reloadAfterSync() {
        try? recomputeStock()
        reloadAll()
    }

    /// Writes a clean copy of the whole database to `url`.
    func backup(to url: URL) throws {
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
        try db.run("VACUUM INTO ?", [url.path])
    }
}

import Foundation

// JSON shapes stored in Supabase (column `data`). Field names are snake_case so
// the Windows app can read the same records.

struct SyncLine: Codable {
    var item_uid: String?
    var code, name, details, unit: String
    var qty, rate, base_rate, cost: Double
}

struct SyncItem: Codable {
    var code, name, details, unit: String
    var image: Data?
    var cost_price, sale_price, stock_adjust, created_at: Double
}

struct SyncPurchaseLine: Codable {
    var item_uid: String?
    var code, name, details, unit: String
    var qty, cost, sale_price: Double
}

struct SyncPurchase: Codable {
    var number, supplier: String
    var date: Double
    var notes: String
    var lines: [SyncPurchaseLine]
}

struct SyncQuotation: Codable {
    var number, po_number, customer_name, customer_address, customer_phone: String
    var date, tax_rate: Double
    var notes: String
    var lines: [SyncLine]
}

struct SyncChallan: Codable {
    var number, quotation_uid, po_number, customer_name, customer_address, customer_phone: String
    var date: Double
    var status: String
    var approved_at: Double?
    var vehicle_no, driver_name, notes: String
    var lines: [SyncLine]
}

struct SyncInvoice: Codable {
    var number, quotation_uid, challan_uid, po_number, customer_name, customer_address, customer_phone: String
    var date, tax_rate: Double
    var notes: String
    var lines: [SyncLine]
}

struct SyncSettings: Codable {
    var values: [String: String]
    var logo: Data?
    var letterhead: Data?
}

/// A local change waiting to be uploaded.
struct OutgoingRow {
    let table: String
    let localId: Int64?
    let uid: String
    let updatedAt: Double
    let data: Data?        // JSON
    let deleted: Bool
    /// Local change number (office sync).
    var seq: Int64 = 0
}

/// A row received from Supabase or from another office computer.
struct IncomingRow {
    let table: String
    let uid: String
    let data: Data?        // JSON
    let updatedAt: Double
    let deleted: Bool
    var seq: Int64 = 0
}

@MainActor
extension AppStore {
    static let syncTables = ["items", "purchases", "quotations", "challans", "invoices", "settings"]
    static let settingsUID = "company-settings"

    private func uidMap(_ table: String) throws -> [Int64: String] {
        var m: [Int64: String] = [:]
        for r in try db.query("SELECT id, uid FROM \(table) WHERE uid IS NOT NULL") { m[r.int("id")] = r.string("uid") }
        return m
    }

    private func idMap(_ table: String) throws -> [String: Int64] {
        var m: [String: Int64] = [:]
        for r in try db.query("SELECT id, uid FROM \(table) WHERE uid IS NOT NULL") { m[r.string("uid")] = r.int("id") }
        return m
    }

    private func syncLines(_ type: String, _ docId: Int64, itemUIDs: [Int64: String]) throws -> [SyncLine] {
        try db.query("SELECT * FROM doc_lines WHERE doc_type=? AND doc_id=? ORDER BY position", [type, docId]).map { r in
            SyncLine(item_uid: r.optInt("item_id").flatMap { itemUIDs[$0] }, code: r.string("code"), name: r.string("name"),
                     details: r.string("details"), unit: r.string("unit"), qty: r.double("qty"), rate: r.double("rate"),
                     base_rate: r.double("base_rate"), cost: r.double("cost"))
        }
    }

    // MARK: - Outgoing

    func pendingCount() -> Int {
        var n = 0
        for t in Schema.syncedTables { n += Int((try? db.query("SELECT COUNT(*) AS n FROM \(t) WHERE dirty = 1"))?.first?.int("n") ?? 0) }
        n += Int((try? db.query("SELECT COUNT(*) AS n FROM tombstones WHERE pushed = 0"))?.first?.int("n") ?? 0)
        if syncValue("settings_dirty") == "1" { n += 1 }
        return n
    }

    /// Changes waiting for the cloud.
    func pendingChanges() throws -> [OutgoingRow] { try exportRows(lanAfter: nil) }

    /// Changes another office computer hasn't seen yet (change number > `after`), oldest first.
    func lanChanges(after: Int64, limit: Int) throws -> (rows: [OutgoingRow], more: Bool) {
        let all = try exportRows(lanAfter: after).sorted { $0.seq < $1.seq }
        return (Array(all.prefix(limit)), all.count > limit)
    }

    private func exportRows(lanAfter: Int64?) throws -> [OutgoingRow] {
        let enc = JSONEncoder()
        let filter = lanAfter == nil ? "dirty = 1 AND uid IS NOT NULL" : "lan_seq > \(lanAfter!) AND uid IS NOT NULL"
        let itemUIDs = try uidMap("items")
        let quoteUIDs = try uidMap("quotations")
        let challanUIDs = try uidMap("challans")
        var out: [OutgoingRow] = []
        func add<T: Encodable>(_ table: String, _ r: Row, _ value: T) throws {
            out.append(OutgoingRow(table: table, localId: r.int("id"), uid: r.string("uid"), updatedAt: r.double("updated_at"),
                                   data: try enc.encode(value), deleted: false, seq: r.int("lan_seq")))
        }

        for r in try db.query("SELECT * FROM items WHERE \(filter)") {
            try add("items", r, SyncItem(code: r.string("code"), name: r.string("name"), details: r.string("details"),
                                         unit: r.string("unit"), image: r.data("image"), cost_price: r.double("cost_price"),
                                         sale_price: r.double("sale_price"), stock_adjust: r.double("stock_adjust"),
                                         created_at: r.double("created_at")))
        }
        for r in try db.query("SELECT * FROM purchases WHERE \(filter)") {
            let lines = try db.query("SELECT * FROM purchase_lines WHERE purchase_id = ? ORDER BY position", [r.int("id")]).map { l in
                SyncPurchaseLine(item_uid: l.optInt("item_id").flatMap { itemUIDs[$0] }, code: l.string("code"), name: l.string("name"),
                                 details: l.string("details"), unit: l.string("unit"), qty: l.double("qty"),
                                 cost: l.double("cost"), sale_price: l.double("sale_price"))
            }
            try add("purchases", r, SyncPurchase(number: r.string("number"), supplier: r.string("supplier"),
                                                 date: r.double("date"), notes: r.string("notes"), lines: lines))
        }
        for r in try db.query("SELECT * FROM quotations WHERE \(filter)") {
            try add("quotations", r, SyncQuotation(number: r.string("number"), po_number: r.string("po_number"),
                customer_name: r.string("customer_name"), customer_address: r.string("customer_address"),
                customer_phone: r.string("customer_phone"), date: r.double("date"), tax_rate: r.double("tax_rate"),
                notes: r.string("notes"), lines: try syncLines("Q", r.int("id"), itemUIDs: itemUIDs)))
        }
        for r in try db.query("SELECT * FROM challans WHERE \(filter)") {
            guard let qUID = quoteUIDs[r.int("quotation_id")] else { continue }
            try add("challans", r, SyncChallan(number: r.string("number"), quotation_uid: qUID, po_number: r.string("po_number"),
                customer_name: r.string("customer_name"), customer_address: r.string("customer_address"),
                customer_phone: r.string("customer_phone"), date: r.double("date"), status: r.string("status"),
                approved_at: r.optDate("approved_at")?.timeIntervalSince1970, vehicle_no: r.string("vehicle_no"),
                driver_name: r.string("driver_name"), notes: r.string("notes"),
                lines: try syncLines("D", r.int("id"), itemUIDs: itemUIDs)))
        }
        for r in try db.query("SELECT * FROM invoices WHERE \(filter)") {
            guard let qUID = quoteUIDs[r.int("quotation_id")], let cUID = challanUIDs[r.int("challan_id")] else { continue }
            try add("invoices", r, SyncInvoice(number: r.string("number"), quotation_uid: qUID, challan_uid: cUID,
                po_number: r.string("po_number"), customer_name: r.string("customer_name"),
                customer_address: r.string("customer_address"), customer_phone: r.string("customer_phone"),
                date: r.double("date"), tax_rate: r.double("tax_rate"), notes: r.string("notes"),
                lines: try syncLines("I", r.int("id"), itemUIDs: itemUIDs)))
        }
        let settingsSeq = Int64(syncValue("settings_lan_seq") ?? "") ?? 0
        let wantSettings = lanAfter.map { settingsSeq > $0 && syncValue("settings_updated") != nil } ?? (syncValue("settings_dirty") == "1")
        if wantSettings {
            let s = settings
            out.append(OutgoingRow(table: "settings", localId: nil, uid: Self.settingsUID,
                                   updatedAt: Double(syncValue("settings_updated") ?? "") ?? Date().timeIntervalSince1970,
                                   data: try enc.encode(SyncSettings(values: try settingsValues(), logo: s.logo, letterhead: s.letterhead)),
                                   deleted: false, seq: settingsSeq))
        }
        let tombFilter = lanAfter.map { "lan_seq > \($0)" } ?? "pushed = 0"
        for r in try db.query("SELECT * FROM tombstones WHERE \(tombFilter)") {
            out.append(OutgoingRow(table: r.string("tbl"), localId: nil, uid: r.string("uid"),
                                   updatedAt: r.double("deleted_at"), data: nil, deleted: true, seq: r.int("lan_seq")))
        }
        return out
    }

    /// Clears the "needs upload" flag, unless the row changed again while uploading.
    func markPushed(_ rows: [OutgoingRow]) throws {
        try db.transaction {
            for r in rows {
                if r.deleted {
                    try db.run("UPDATE tombstones SET pushed = 1 WHERE tbl = ? AND uid = ?", [r.table, r.uid])
                } else if r.table == "settings" {
                    if syncValue("settings_updated") == nil || Double(syncValue("settings_updated") ?? "") == r.updatedAt {
                        try setSyncValue("settings_dirty", "0")
                    }
                } else if let id = r.localId {
                    try db.run("UPDATE \(r.table) SET dirty = 0 WHERE id = ? AND updated_at = ?", [id, r.updatedAt])
                }
            }
        }
    }

    // MARK: - Incoming

    /// Applies downloaded rows. Newer local edits win over older remote ones.
    /// Returns messages for rows that could not be applied.
    func applyRemote(_ rows: [IncomingRow], fromLAN: Bool = false) throws -> (problems: [String], failed: [IncomingRow], applied: Int) {
        var problems: [String] = []
        var failed: [IncomingRow] = []
        var applied = 0
        let dec = JSONDecoder()
        let order = ["items", "purchases", "quotations", "challans", "invoices", "settings"]
        let upserts = rows.filter { !$0.deleted }.sorted { order.firstIndex(of: $0.table)! < order.firstIndex(of: $1.table)! }
        let deletes = rows.filter { $0.deleted }.sorted { order.firstIndex(of: $0.table)! > order.firstIndex(of: $1.table)! }

        try db.transaction {
            for row in upserts {
                try db.exec("SAVEPOINT remote_row;")
                do {
                    if try applyUpsert(row, dec) {
                        applied += 1
                        // Pass it on to other office computers; changes that came over the office
                        // network are also queued for the cloud.
                        if row.table == "settings" {
                            try setSyncValue("settings_lan_seq", String(try nextLANSeq()))
                            if fromLAN { try setSyncValue("settings_dirty", "1") }
                        } else {
                            try bumpLAN(row.table, uid: row.uid, dirty: fromLAN)
                        }
                    }
                    try db.exec("RELEASE remote_row;")
                } catch {
                    try? db.exec("ROLLBACK TO remote_row;")
                    try? db.exec("RELEASE remote_row;")
                    problems.append("\(row.table) \(row.uid.prefix(8)): \(error.localizedDescription)")
                    failed.append(row)
                }
            }
            for row in deletes {
                do { try applyDelete(row, fromLAN: fromLAN) } catch {
                    problems.append("delete \(row.table) \(row.uid.prefix(8)): \(error.localizedDescription)")
                }
            }
            try recomputeStock()
        }
        return (problems, failed, applied + deletes.count)
    }

    /// Returns false when the row was skipped (we already have this or a newer version).
    private func applyUpsert(_ row: IncomingRow, _ dec: JSONDecoder) throws -> Bool {
        guard let data = row.data else { return false }
        if row.table == "settings" {
            let localStamp = Double(syncValue("settings_updated") ?? "") ?? 0
            if localStamp == row.updatedAt { return false }
            if syncValue("settings_dirty") == "1" && localStamp >= row.updatedAt { return false }
            let s = try dec.decode(SyncSettings.self, from: data)
            for (k, v) in s.values { try db.run("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", [k, v]) }
            try db.run("INSERT OR REPLACE INTO settings (key, data) VALUES ('logo', ?)", [s.logo])
            try db.run("INSERT OR REPLACE INTO settings (key, data) VALUES ('letterhead', ?)", [s.letterhead])
            try setSyncValue("settings_updated", String(row.updatedAt))
            try setSyncValue("settings_dirty", "0")
            return true
        }
        let local = try db.query("SELECT id, dirty, updated_at FROM \(row.table) WHERE uid = ?", [row.uid]).first
        if let local {
            if local.double("updated_at") >= row.updatedAt { return false }   // same or newer version here
        }
        let id = local?.int("id")
        switch row.table {
        case "items": try applyItem(try dec.decode(SyncItem.self, from: data), row, id)
        case "purchases": try applyPurchase(try dec.decode(SyncPurchase.self, from: data), row, id)
        case "quotations": try applyQuotation(try dec.decode(SyncQuotation.self, from: data), row, id)
        case "challans": try applyChallan(try dec.decode(SyncChallan.self, from: data), row, id)
        case "invoices": try applyInvoice(try dec.decode(SyncInvoice.self, from: data), row, id)
        default: return false
        }
        return true
    }

    /// Two computers used the same Item ID / document number offline. The record with the
    /// smaller uid keeps the name; the other gets "-2". Both computers decide the same way.
    private func resolveUnique(_ table: String, _ column: String, _ value: String, uid: String) throws -> String {
        guard let other = try db.query("SELECT id, uid FROM \(table) WHERE \(column) = ? COLLATE NOCASE AND uid <> ?",
                                       [value, uid]).first else { return value }
        if uid < other.string("uid") {
            try db.run("UPDATE \(table) SET \(column) = \(column) || '-2' WHERE id = ?", [other.int("id")])
            try touch(table, other.int("id"))
            return value
        }
        return value + "-2"
    }

    private func applyItem(_ d: SyncItem, _ row: IncomingRow, _ id: Int64?) throws {
        let code = try resolveUnique("items", "code", d.code, uid: row.uid)
        if let id {
            try db.run("""
                UPDATE items SET code=?, name=?, details=?, unit=?, image=?, cost_price=?, sale_price=?, stock_adjust=?,
                    updated_at=?, dirty=0 WHERE id=?
                """, [code, d.name, d.details, d.unit, d.image, d.cost_price, d.sale_price, d.stock_adjust, row.updatedAt, id])
        } else {
            try db.run("""
                INSERT INTO items (code, name, details, unit, image, cost_price, sale_price, stock, stock_adjust, created_at,
                    uid, updated_at, dirty) VALUES (?,?,?,?,?,?,?,0,?,?,?,?,0)
                """, [code, d.name, d.details, d.unit, d.image, d.cost_price, d.sale_price, d.stock_adjust, d.created_at,
                      row.uid, row.updatedAt])
        }
    }

    private func applyPurchase(_ d: SyncPurchase, _ row: IncomingRow, _ id: Int64?) throws {
        let items = try idMap("items")
        var pid = id ?? 0
        if let id {
            try db.run("UPDATE purchases SET number=?, supplier=?, date=?, notes=?, updated_at=?, dirty=0 WHERE id=?",
                       [d.number, d.supplier, d.date, d.notes, row.updatedAt, id])
            try db.run("DELETE FROM purchase_lines WHERE purchase_id = ?", [id])
        } else {
            pid = try db.run("INSERT INTO purchases (number, supplier, date, notes, uid, updated_at, dirty) VALUES (?,?,?,?,?,?,0)",
                             [d.number, d.supplier, d.date, d.notes, row.uid, row.updatedAt])
        }
        for (pos, l) in d.lines.enumerated() {
            try db.run("""
                INSERT INTO purchase_lines (purchase_id, item_id, code, name, details, unit, qty, cost, sale_price, position)
                VALUES (?,?,?,?,?,?,?,?,?,?)
                """, [pid, l.item_uid.flatMap { items[$0] }, l.code, l.name, l.details, l.unit, l.qty, l.cost, l.sale_price, pos])
        }
    }

    private func writeLines(_ type: String, _ docId: Int64, _ lines: [SyncLine]) throws {
        let items = try idMap("items")
        try db.run("DELETE FROM doc_lines WHERE doc_type = ? AND doc_id = ?", [type, docId])
        for (pos, l) in lines.enumerated() {
            try db.run("""
                INSERT INTO doc_lines (doc_type, doc_id, item_id, code, name, details, unit, qty, rate, base_rate, cost, position)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                """, [type, docId, l.item_uid.flatMap { items[$0] }, l.code, l.name, l.details, l.unit, l.qty, l.rate,
                      l.base_rate, l.cost, pos])
        }
    }

    private func applyQuotation(_ d: SyncQuotation, _ row: IncomingRow, _ id: Int64?) throws {
        let number = try resolveUnique("quotations", "number", d.number, uid: row.uid)
        let p: [Any?] = [number, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date, d.tax_rate,
                         d.notes, row.updatedAt]
        var qid = id ?? 0
        if let id {
            try db.run("""
                UPDATE quotations SET number=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?, date=?,
                    tax_rate=?, notes=?, updated_at=?, dirty=0 WHERE id=?
                """, p + [id])
        } else {
            qid = try db.run("""
                INSERT INTO quotations (number, po_number, customer_name, customer_address, customer_phone, date, tax_rate,
                    notes, updated_at, uid, dirty) VALUES (?,?,?,?,?,?,?,?,?,?,0)
                """, p + [row.uid])
        }
        try writeLines("Q", qid, d.lines)
    }

    private func applyChallan(_ d: SyncChallan, _ row: IncomingRow, _ id: Int64?) throws {
        guard let qid = try idMap("quotations")[d.quotation_uid] else {
            throw AppError("its quotation has not arrived yet")
        }
        let number = try resolveUnique("challans", "number", d.number, uid: row.uid)
        let p: [Any?] = [number, qid, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date, d.status,
                         d.approved_at, d.vehicle_no, d.driver_name, d.notes, row.updatedAt]
        var cid = id ?? 0
        if let id {
            try db.run("""
                UPDATE challans SET number=?, quotation_id=?, po_number=?, customer_name=?, customer_address=?, customer_phone=?,
                    date=?, status=?, approved_at=?, vehicle_no=?, driver_name=?, notes=?, updated_at=?, dirty=0 WHERE id=?
                """, p + [id])
        } else {
            cid = try db.run("""
                INSERT INTO challans (number, quotation_id, po_number, customer_name, customer_address, customer_phone, date,
                    status, approved_at, vehicle_no, driver_name, notes, updated_at, uid, dirty)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,0)
                """, p + [row.uid])
        }
        try writeLines("D", cid, d.lines)
    }

    private func applyInvoice(_ d: SyncInvoice, _ row: IncomingRow, _ id: Int64?) throws {
        guard let qid = try idMap("quotations")[d.quotation_uid], let cid = try idMap("challans")[d.challan_uid] else {
            throw AppError("its quotation or delivery challan has not arrived yet")
        }
        let number = try resolveUnique("invoices", "number", d.number, uid: row.uid)
        let p: [Any?] = [number, qid, cid, d.po_number, d.customer_name, d.customer_address, d.customer_phone, d.date,
                         d.tax_rate, d.notes, row.updatedAt]
        var iid = id ?? 0
        if let id {
            try db.run("""
                UPDATE invoices SET number=?, quotation_id=?, challan_id=?, po_number=?, customer_name=?, customer_address=?,
                    customer_phone=?, date=?, tax_rate=?, notes=?, updated_at=?, dirty=0 WHERE id=?
                """, p + [id])
        } else {
            iid = try db.run("""
                INSERT INTO invoices (number, quotation_id, challan_id, po_number, customer_name, customer_address,
                    customer_phone, date, tax_rate, notes, updated_at, uid, dirty) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,0)
                """, p + [row.uid])
        }
        try writeLines("I", iid, d.lines)
    }

    private func applyDelete(_ row: IncomingRow, fromLAN: Bool) throws {
        guard row.table != "settings" else { return }
        // Remember the deletion so it is passed on to other office computers.
        if try db.query("SELECT 1 FROM tombstones WHERE tbl = ? AND uid = ?", [row.table, row.uid]).isEmpty {
            try db.run("INSERT INTO tombstones (tbl, uid, deleted_at, pushed, lan_seq) VALUES (?,?,?,?,?)",
                       [row.table, row.uid, row.updatedAt, fromLAN ? 0 : 1, try nextLANSeq()])
        }
        guard let local = try db.query("SELECT id, dirty, updated_at FROM \(row.table) WHERE uid = ?", [row.uid]).first else { return }
        if local.int("dirty") == 1 && local.double("updated_at") > row.updatedAt { return }   // edited here after the delete
        let id = local.int("id")
        switch row.table {
        case "purchases": try db.run("DELETE FROM purchase_lines WHERE purchase_id = ?", [id])
        case "quotations": try db.run("DELETE FROM doc_lines WHERE doc_type='Q' AND doc_id=?", [id])
        case "challans": try db.run("DELETE FROM doc_lines WHERE doc_type='D' AND doc_id=?", [id])
        case "invoices": try db.run("DELETE FROM doc_lines WHERE doc_type='I' AND doc_id=?", [id])
        default: break
        }
        try db.run("DELETE FROM \(row.table) WHERE id = ?", [id])
    }
}

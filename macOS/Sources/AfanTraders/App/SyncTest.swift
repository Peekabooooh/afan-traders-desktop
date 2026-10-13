import Foundation

/// `AfanTraders --synctest <folder>`: two computers (two databases) syncing through
/// an in-memory stand-in for Supabase that follows the same rules as the setup SQL.
@MainActor
enum SyncTest {
    final class FakeServer {
        struct Rec { var data: Data?; var updatedAt: Double; var deleted: Bool; var seq: Int }
        var tables: [String: [String: Rec]] = [:]
        var seq = 0

        func push(_ rows: [OutgoingRow]) {
            for r in rows {
                if let old = tables[r.table]?[r.uid], old.updatedAt > r.updatedAt { continue }   // newer wins
                seq += 1
                tables[r.table, default: [:]][r.uid] = Rec(data: r.data, updatedAt: r.updatedAt, deleted: r.deleted, seq: seq)
            }
        }

        func pull(after cursor: Int) -> [IncomingRow] {
            tables.flatMap { t, recs in
                recs.filter { $0.value.seq > cursor }.map {
                    IncomingRow(table: t, uid: $0.key, data: $0.value.data, updatedAt: $0.value.updatedAt, deleted: $0.value.deleted)
                }
            }
        }
    }

    static func run(folder: URL) -> Never {
        func check(_ ok: Bool, _ what: String) {
            print(ok ? "PASS  \(what)" : "FAIL  \(what)")
            if !ok { exit(1) }
        }
        try? FileManager.default.removeItem(at: folder)
        let a = AppStore(folder: folder.appendingPathComponent("A"))
        let b = AppStore(folder: folder.appendingPathComponent("B"))
        let server = FakeServer()
        var cursors: [ObjectIdentifier: Int] = [:]

        func sync(_ s: AppStore) throws {
            let start = server.seq
            let rows = server.pull(after: cursors[ObjectIdentifier(s)] ?? 0)
            if !rows.isEmpty {
                let problems = try s.applyRemote(rows).problems
                if !problems.isEmpty { print("      problems: \(problems)") }
                s.reloadAfterSync()
            }
            cursors[ObjectIdentifier(s)] = start
            let out = try s.pendingChanges()
            server.push(out)
            try s.markPushed(out)
        }
        func syncAll() throws { for _ in 0..<2 { try sync(a); try sync(b) } }

        do {
            // A enters everything.
            try a.savePurchase(Purchase(number: "P-1", supplier: "Sup", lines: [
                PurchaseLine(code: "X1", name: "Cable", qty: 10, cost: 100, salePrice: 150),
                PurchaseLine(code: "X2", name: "Switch", qty: 50, cost: 10, salePrice: 20),
            ]))
            let x1 = a.item(code: "X1")!
            var q = a.draftQuotation()
            q.customerName = "Customer One"; q.poNumber = "PO-9"
            q.lines = [DocLine(itemId: x1.id, code: "X1", name: "Cable", qty: 3, rate: 150, baseRate: 150)]
            q = try a.saveQuotation(q)
            let c = try a.saveChallan(a.draftChallan(from: q))
            try a.setApproved(c, true)
            _ = try a.saveInvoice(try a.draftInvoice(from: a.challan(id: c.id)!))
            check(a.item(code: "X1")?.stock == 7, "A: stock 10 − 3 delivered = 7")
            check(a.pendingCount() > 0, "A: changes waiting to upload (\(a.pendingCount()))")

            try syncAll()
            check(a.pendingCount() == 0, "A: nothing waiting after sync")
            check(b.items.count == 2 && b.purchases.count == 1 && b.quotations.count == 1, "B: received items, purchase, quotation")
            check(b.challans.first?.status == .approved && b.invoices.count == 1, "B: received approved challan and invoice")
            check(b.item(code: "X1")?.stock == 7, "B: same stock as A (7)")
            check(b.invoices.first?.poNumber == "PO-9", "B: PO number linked")

            // Both computers buy more of X1 while offline.
            try a.savePurchase(Purchase(number: "P-2", lines: [PurchaseLine(itemId: x1.id, code: "X1", name: "", qty: 5, cost: 100)]))
            let bx1 = b.item(code: "X1")!
            try b.savePurchase(Purchase(number: "P-3", lines: [PurchaseLine(itemId: bx1.id, code: "X1", name: "", qty: 8, cost: 100)]))
            try syncAll()
            check(a.item(code: "X1")?.stock == 20 && b.item(code: "X1")?.stock == 20,
                  "simultaneous stock-in on both: 7 + 5 + 8 = 20 on both (A \(a.item(code: "X1")!.stock), B \(b.item(code: "X1")!.stock))")

            // Same quotation edited on both; the later edit wins everywhere.
            var qa = a.quotations.first!; qa.customerName = "Edited on A"; _ = try a.saveQuotation(qa)
            Thread.sleep(forTimeInterval: 0.01)
            var qb = b.quotations.first!; qb.customerName = "Edited on B (later)"; _ = try b.saveQuotation(qb)
            try syncAll()
            check(a.quotations.first?.customerName == "Edited on B (later)" && b.quotations.first?.customerName == "Edited on B (later)",
                  "same quotation edited on both: newer edit wins on both")

            // Same Item ID created on both while offline.
            try a.saveItem(Item(code: "DUP", name: "Made on A"))
            try b.saveItem(Item(code: "DUP", name: "Made on B"))
            try syncAll()
            let codesA = Set(a.items.filter { $0.code.hasPrefix("DUP") }.map { "\($0.code)=\($0.name)" })
            let codesB = Set(b.items.filter { $0.code.hasPrefix("DUP") }.map { "\($0.code)=\($0.name)" })
            check(codesA.count == 2 && codesA == codesB, "same Item ID on both: kept both, same names everywhere \(codesA.sorted())")

            // Delete on B reaches A.
            try b.deleteInvoice(b.invoices.first!)
            try syncAll()
            check(a.invoices.isEmpty, "invoice deleted on B is deleted on A")
            try b.deleteChallan(b.challans.first!)
            try syncAll()
            check(a.challans.isEmpty && a.item(code: "X1")?.stock == 23, "challan deleted on B: A gets the 3 back into stock (23)")

            // Settings.
            var st = a.settings; st.companyName = "AFAN TRADERS (Lahore)"; st.defaultTaxRate = 17
            try a.saveSettings(st)
            try syncAll()
            check(b.settings.companyName == "AFAN TRADERS (Lahore)" && b.settings.defaultTaxRate == 17, "settings synced to B")

            // Manual stock correction on one computer.
            var fix = b.item(code: "X2")!; fix.stock = 45; try b.saveItem(fix)
            try syncAll()
            check(a.item(code: "X2")?.stock == 45, "manual stock correction on B shows on A (45)")

            print("ALL PASSED")
            exit(0)
        } catch {
            print("FAIL  \(error.localizedDescription)")
            exit(1)
        }
    }
}

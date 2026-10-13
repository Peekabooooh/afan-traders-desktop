import Foundation
import Network

/// `AfanTraders --lantest <folder>`: several "computers" (databases) syncing over real
/// TCP connections on this Mac, with the office-code check.
@MainActor
enum LANTest {
    static func run(folder: URL) -> Never {
        func check(_ ok: Bool, _ what: String) {
            print(ok ? "PASS  \(what)" : "FAIL  \(what)")
            if !ok { exit(1) }
        }
        func wait(_ seconds: Double = 8, _ cond: () -> Bool) -> Bool {
            let end = Date().addingTimeInterval(seconds)
            while Date() < end {
                if cond() { return true }
                RunLoop.main.run(until: Date().addingTimeInterval(0.05))
            }
            return cond()
        }
        func link(_ x: AppStore, _ y: AppStore) {
            x.lan.connect(to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: y.lan.port!)!))
        }
        try? FileManager.default.removeItem(at: folder)
        let a = AppStore(folder: folder.appendingPathComponent("A"))
        let b = AppStore(folder: folder.appendingPathComponent("B"))
        let c = AppStore(folder: folder.appendingPathComponent("C-wrong-code"))
        let d = AppStore(folder: folder.appendingPathComponent("D"))
        do {
            try a.savePurchase(Purchase(number: "P-1", lines: [
                PurchaseLine(code: "X1", name: "Cable", qty: 10, cost: 100, salePrice: 150),
            ]))
            var q = a.draftQuotation()
            q.customerName = "Customer One"; q.poNumber = "PO-1"
            let x1 = a.item(code: "X1")!
            q.lines = [DocLine(itemId: x1.id, code: "X1", name: "Cable", qty: 3, rate: 150, baseRate: 150)]
            _ = try a.saveQuotation(q)

            for s in [a, b, d] { s.lan.startForTest(code: "afan-1234") }
            c.lan.startForTest(code: "wrong-code")
            check(wait { [a, b, c, d].allSatisfy { $0.lan.listening && ($0.lan.port ?? 0) > 0 } }, "all computers listening")
            link(a, b); link(b, a)

            let got = wait { b.item(code: "X1")?.stock == 10 && b.quotations.count == 1 }
            if !got {
                print("  B peers: \(b.lan.peers.values.map { "\($0.name) connected=\($0.connected)" })  problems: \(b.lan.problems)")
                print("  A peers: \(a.lan.peers.values.map { "\($0.name) connected=\($0.connected)" })  problems: \(a.lan.problems)")
                print("  B items: \(b.items.map { "\($0.code)=\($0.stock)" })  quotes: \(b.quotations.count)")
                print("  A lanChanges: \((try? a.lanChanges(after: 0, limit: 50).rows.map { "\($0.table):\($0.seq)" }) ?? [])")
            }
            check(got, "B received A's stock (10) and quotation")
            check(b.lan.peers.values.contains { $0.connected }, "B shows A as connected: \(b.lan.statusText)")

            // Change on B arrives on A straight away (no polling).
            let bq = b.quotations.first!
            let ch = try b.saveChallan(b.draftChallan(from: bq))
            try b.setApproved(ch, true)
            check(wait(3) { a.challans.first?.status == .approved && a.item(code: "X1")?.stock == 7 },
                  "B approves challan → A sees it within 3 s, stock 7")

            // Both add stock at the same time.
            try a.savePurchase(Purchase(number: "P-2", lines: [PurchaseLine(itemId: x1.id, code: "X1", name: "", qty: 5, cost: 100)]))
            let bx = b.item(code: "X1")!
            try b.savePurchase(Purchase(number: "P-3", lines: [PurchaseLine(itemId: bx.id, code: "X1", name: "", qty: 8, cost: 100)]))
            check(wait { a.item(code: "X1")?.stock == 20 && b.item(code: "X1")?.stock == 20 },
                  "both add stock at once: 7 + 5 + 8 = 20 on both")

            // Delete travels.
            try a.deletePurchase(a.purchases.first { $0.number == "P-2" }!)
            check(wait { b.purchases.count == 2 && b.item(code: "X1")?.stock == 15 }, "purchase deleted on A is gone on B, stock 15")

            // Settings travel.
            var st = a.settings; st.companyName = "AFAN TRADERS – Office"; try a.saveSettings(st)
            check(wait { b.settings.companyName == "AFAN TRADERS – Office" }, "settings synced")

            // Wrong office code is refused.
            link(c, a)
            check(wait(3) { !c.lan.problems.isEmpty }, "wrong office code refused: \(c.lan.problems.first ?? "-")")
            check(c.items.isEmpty && c.quotations.isEmpty, "computer with wrong code received nothing")

            // D only talks to B but still gets everything A made (passed along by B).
            link(d, b); link(b, d)
            check(wait { d.item(code: "X1")?.stock == 15 && d.quotations.count == 1 && d.challans.count == 1
                         && d.settings.companyName == "AFAN TRADERS – Office" },
                  "third computer linked only to B gets A's data too")
            try d.saveItem(Item(code: "NEW-D", name: "Made on D"))
            check(wait { a.item(code: "NEW-D") != nil }, "item made on D reaches A through B")

            // Changes from the office network are queued for the cloud as well.
            check(d.pendingCount() > 0, "office changes also queued for cloud upload (\(d.pendingCount()))")
            print("ALL PASSED")
            exit(0)
        } catch {
            print("FAIL  \(error.localizedDescription)")
            exit(1)
        }
    }
}

import Foundation

/// `AfanTraders --selftest <folder>` runs the whole flow on a scratch database
/// in <folder>, writes the three PDFs there, and exits. Used to verify builds.
@MainActor
enum SelfTest {
    static func run(folder: URL) -> Never {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let store = AppStore(folder: folder)
        func check(_ ok: Bool, _ what: String) {
            print(ok ? "PASS  \(what)" : "FAIL  \(what)")
            if !ok { exit(1) }
        }
        do {
            // Stock in through a purchase: one new item, then the same item again.
            do { try store.savePurchase(Purchase(lines: [PurchaseLine(code: "X", name: "X", qty: 1)])); check(false, "invoice No. required") }
            catch { check(true, "purchase invoice number required") }
            try store.savePurchase(Purchase(number: "PINV-501", supplier: "Test Supplier", lines: [
                PurchaseLine(code: "CBL-25", name: "Copper Cable 2.5mm", details: "Pakistan Cables, 90m coil",
                             unit: "Coil", qty: 10, cost: 9000, salePrice: 10500),
                PurchaseLine(code: "SW-1G", name: "Switch 1 Gang", unit: "Pcs", qty: 100, cost: 150, salePrice: 220),
                PurchaseLine(qty: 0), PurchaseLine(qty: 0),
            ]))
            check(store.items.count == 2, "bulk add: 2 items created, blank rows ignored")
            let cable = store.item(code: "cbl-25")!
            try store.savePurchase(Purchase(number: "PINV-502", supplier: "Test Supplier", lines: [
                PurchaseLine(itemId: cable.id, code: cable.code, name: cable.name, qty: 5, cost: 9200, salePrice: 0),
            ]))
            check(store.item(code: "CBL-25")?.stock == 15, "purchase adds stock to existing item (15)")
            check(store.item(code: "CBL-25")?.costPrice == 9200, "latest cost price kept")
            let hist = store.stockHistory(of: store.item(code: "CBL-25")!).map(\.reference).sorted()
            check(hist == ["Purchase Inv. PINV-501", "Purchase Inv. PINV-502"], "stock linked to both purchase invoices")
            check(store.item(code: "CBL-25")?.salePrice == 10500, "sale price kept when purchase leaves it 0")

            let sw = store.item(code: "SW-1G")!
            var q = store.draftQuotation()
            check(q.taxRate == 18, "default sales tax 18%")
            q.customerName = "ABC Industries (Pvt) Ltd"
            q.customerAddress = "Plot 7, Sundar Industrial Estate, Lahore"
            q.poNumber = "PO-7781"
            q.lines = [
                DocLine(itemId: cable.id, code: cable.code, name: cable.name, details: cable.details,
                        unit: cable.unit, qty: 4, rate: 10500, baseRate: 10500),
                DocLine(itemId: sw.id, code: sw.code, name: sw.name, unit: sw.unit, qty: 40, rate: 220, baseRate: 220),
            ]
            for i in 1...28 {
                q.lines.append(DocLine(code: "MISC-\(i)", name: "Fitting item \(i)", details: i % 3 == 0 ? "Extra detail line" : "",
                                       qty: Double(i), rate: 100, baseRate: 100))
            }
            q = try store.saveQuotation(q)
            check(q.subtotal == 42000 + 8800 + 40600, "quotation subtotal")
            check(q.taxAmount == (91400 * 0.18).rounded2, "sales tax amount")
            check(store.stage(of: q) == .needsChallan, "stage: needs challan")

            let c = try store.saveChallan(store.draftChallan(from: q))
            check(c.poNumber == "PO-7781", "challan carries PO number")
            check(store.stage(of: q) == .awaitingApproval, "stage: awaiting approval")
            do { _ = try store.draftInvoice(from: c); check(false, "invoice blocked before approval") }
            catch { check(true, "invoice blocked before approval") }

            try store.setApproved(c, true)
            check(store.item(code: "CBL-25")?.stock == 11, "approval removes stock (15 → 11)")
            check(store.stage(of: q) == .readyToInvoice, "stage: ready to invoice")

            var inv = try store.draftInvoice(from: store.challan(id: c.id)!)
            check(inv.lines[0].rate == 10500, "invoice starts at quotation price")
            inv.lines[0].rate = 11000
            inv = try store.saveInvoice(inv)
            check(inv.subtotal == q.subtotal + 2000, "invoice price changed for margin")
            check(store.stage(of: q) == .invoiced, "stage: invoiced")
            check(store.invoice(forQuotation: q.id)?.poNumber == "PO-7781", "invoice carries PO number")

            q.poNumber = "PO-7781-A"
            q = try store.saveQuotation(q)
            check(store.challan(id: c.id)?.poNumber == "PO-7781-A" && store.invoice(forQuotation: q.id)?.poNumber == "PO-7781-A",
                  "PO change flows to challan and invoice")

            do { try store.setApproved(c, false); check(false, "cannot un-approve invoiced challan") }
            catch { check(true, "cannot un-approve invoiced challan") }

            // Reports
            let all = RangePreset.allTime.interval(from: Date(), to: Date())
            let ov = store.overview(all)
            check(ov.netSales == inv.subtotal, "report: net sales match invoice")
            let costAtSale = 4 * 9200.0 + 40 * 150.0   // misc lines have no cost
            check(ov.cost == costAtSale, "report: cost of goods uses cost at time of sale (\(ov.cost))")
            var dearer = store.item(code: "CBL-25")!
            dearer.costPrice = 9999
            try store.saveItem(dearer)
            check(store.overview(all).cost == costAtSale, "report: later cost change does not alter old profit")
            let items = store.itemsReport(all)
            check(items.rows.first { $0.cells[0].text == "CBL-25" }?.cells[3].value == 4, "report: item by item qty sold")
            check(store.billsReport(all).rows.count == 1 && store.lineReport(all).rows.count == 30, "report: bills and line detail")
            let daily = store.dailyReport(all)
            check(daily.totals(for: daily.rows)?[3].value == inv.subtotal, "report: day by day totals")
            check(!daily.csv(rows: daily.rows, includeSensitive: false).contains("Profit"), "report: Excel export hides cost when hidden")
            let pdf = ReportPDF.render(store.lineReport(all), rows: store.lineReport(all).rows, period: "All time",
                                       settings: store.settings, showCost: true)
            try pdf.write(to: folder.appendingPathComponent("report-lines.pdf"))
            check(pdf.count > 5000, "report PDF rendered")

            // Sample history so the reports have something to show.
            let customers = ["Nishat Mills", "Packages Ltd", "Engro Foods", "Lucky Cement", "Interloop"]
            let cal = Calendar.current
            for day in stride(from: 24, through: 1, by: -2) {
                let date = cal.date(byAdding: .day, value: -day, to: Date())!
                var sq = store.draftQuotation()
                sq.date = date
                sq.customerName = customers[day % customers.count]
                sq.poNumber = "PO-\(3000 + day)"
                let c1 = store.item(code: "CBL-25")!, s1 = store.item(code: "SW-1G")!
                sq.lines = [
                    DocLine(itemId: c1.id, code: c1.code, name: c1.name, unit: c1.unit, qty: Double(1 + day % 3), rate: 10500, baseRate: 10500),
                    DocLine(itemId: s1.id, code: s1.code, name: s1.name, unit: s1.unit, qty: Double(10 + day * 3), rate: 220, baseRate: 220),
                ]
                sq = try store.saveQuotation(sq)
                var sc = store.draftChallan(from: sq); sc.date = date
                sc = try store.saveChallan(sc)
                try store.setApproved(sc, true)
                var si = try store.draftInvoice(from: store.challan(id: sc.id)!)
                si.date = date
                si.lines[0].rate = 10500 + Double(day % 4) * 250
                _ = try store.saveInvoice(si)
            }
            try store.savePurchase(Purchase(number: "PINV-777", supplier: "Pak Cables", date: cal.date(byAdding: .day, value: -10, to: Date())!,
                                            lines: [PurchaseLine(itemId: store.item(code: "CBL-25")!.id, code: "CBL-25", name: "", qty: 30, cost: 9999, salePrice: 0)]))
            check(store.billsReport(all).rows.count == 13, "sample history created")
            // Two open orders: one needing a delivery challan, one awaiting approval.
            for (i, name) in ["Open Order Co", "Waiting Approval Co"].enumerated() {
                var oq = store.draftQuotation()
                oq.customerName = name
                oq.lines = [DocLine(itemId: sw.id, code: sw.code, name: sw.name, qty: 5, rate: 220, baseRate: 220)]
                oq = try store.saveQuotation(oq)
                if i == 1 { try store.saveChallan(store.draftChallan(from: oq)) }
            }
            check(store.quotations.filter { store.stage(of: $0) == .needsChallan }.count == 1, "open order needing DC")

            let docs: [(String, PrintableDoc)] = [
                ("quotation.pdf", store.printable(q)),
                ("challan.pdf", store.printable(store.challan(id: c.id)!)),
                ("invoice.pdf", store.printable(inv)),
            ]
            for (name, doc) in docs {
                let data = PDFRenderer.render(doc, settings: store.settings)
                try data.write(to: folder.appendingPathComponent(name))
                check(data.count > 5000, "\(name) rendered (\(data.count / 1024) KB, \(PDFRenderer.paginate(doc, settings: store.settings).count) pages)")
            }
            print("ALL PASSED")
            exit(0)
        } catch {
            print("FAIL  \(error.localizedDescription)")
            exit(1)
        }
    }
}

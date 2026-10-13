using System.Text.Json;
using AfanTraders.Core;

// AfanTraders.Tests – runs on any OS (the shared logic of the Windows app).
// Usage: dotnet run -- <selftest|synctest|lantest|macdb|maclan|pdf> <folder> [args]
var test = args.Length > 0 ? args[0] : "selftest";
var folder = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "afan-tests");
try
{
    return test switch
    {
        "selftest" => Tests.SelfTest(folder),
        "synctest" => Tests.SyncTest(folder),
        "lantest" => Tests.LanTest(folder),
        "macdb" => Tests.MacDb(folder, args[2]),
        "maclan" => Tests.MacLan(folder, args[2], int.Parse(args[3])),
        "pdf" => Tests.Pdf(folder, args.Skip(2).ToArray()),
        "resources" => Tests.Resources(folder),
        _ => Tests.Fail($"unknown test {test}"),
    };
}
catch (Exception e) { return Tests.Fail($"{e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }

static partial class Tests
{
    public static int Fail(string m) { Console.WriteLine("FAIL  " + m); return 1; }

    static void Check(bool ok, string what)
    {
        Console.WriteLine(ok ? $"PASS  {what}" : $"FAIL  {what}");
        if (!ok) Environment.Exit(1);
    }

    static void Throws(Action a, string what)
    {
        try { a(); Check(false, what); } catch (AppError) { Check(true, what); }
    }

    static bool Wait(Func<bool> cond, double seconds = 8)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    static string Fresh(string folder) { if (Directory.Exists(folder)) Directory.Delete(folder, true); return folder; }

    // Same checks as the Mac app's SelfTest.swift.
    public static int SelfTest(string folder)
    {
        using var store = new Store(Fresh(folder));
        Throws(() => store.SavePurchase(new Purchase { Lines = new[] { new PurchaseLine { Code = "X", Name = "X", Qty = 1 } } }), "purchase invoice number required");
        store.SavePurchase(new Purchase
        {
            Number = "PINV-501", Supplier = "Test Supplier", Lines = new[]
            {
                new PurchaseLine { Code = "CBL-25", Name = "Copper Cable 2.5mm", Details = "Pakistan Cables, 90m coil", Unit = "Coil", Qty = 10, Cost = 9000, SalePrice = 10500 },
                new PurchaseLine { Code = "SW-1G", Name = "Switch 1 Gang", Unit = "Pcs", Qty = 100, Cost = 150, SalePrice = 220 },
                new PurchaseLine { Qty = 0 }, new PurchaseLine { Qty = 0 },
            },
        });
        Check(store.Items.Count == 2, "bulk add: 2 items created, blank rows ignored");
        var cable = store.ItemByCode("cbl-25")!;
        store.SavePurchase(new Purchase { Number = "PINV-502", Supplier = "Test Supplier", Lines = new[] { new PurchaseLine { ItemId = cable.Id, Code = cable.Code, Name = cable.Name, Qty = 5, Cost = 9200, SalePrice = 0 } } });
        Check(store.ItemByCode("CBL-25")!.Stock == 15, "purchase adds stock to existing item (15)");
        Check(store.ItemByCode("CBL-25")!.CostPrice == 9200, "latest cost price kept");
        Check(store.ItemByCode("CBL-25")!.SalePrice == 10500, "sale price kept when purchase leaves it 0");
        var hist = store.StockHistory(store.ItemByCode("CBL-25")!).Select(m => m.Reference).OrderBy(x => x).ToList();
        Check(hist.SequenceEqual(new[] { "Purchase Inv. PINV-501", "Purchase Inv. PINV-502" }), "stock linked to both purchase invoices");

        var sw = store.ItemByCode("SW-1G")!;
        var q = store.DraftQuotation();
        Check(q.TaxRate == 18, "default sales tax 18%");
        var lines = new List<DocLine>
        {
            new() { ItemId = cable.Id, Code = cable.Code, Name = cable.Name, Details = cable.Details, Unit = cable.Unit, Qty = 4, Rate = 10500, BaseRate = 10500 },
            new() { ItemId = sw.Id, Code = sw.Code, Name = sw.Name, Unit = sw.Unit, Qty = 40, Rate = 220, BaseRate = 220 },
        };
        for (int i = 1; i <= 28; i++) lines.Add(new DocLine { Code = $"MISC-{i}", Name = $"Fitting item {i}", Details = i % 3 == 0 ? "Extra detail line" : "", Qty = i, Rate = 100, BaseRate = 100 });
        q = store.SaveQuotation(q with { CustomerName = "ABC Industries (Pvt) Ltd", CustomerAddress = "Plot 7, Sundar Industrial Estate, Lahore", PoNumber = "PO-7781", Lines = lines });
        Check(q.Subtotal() == 42000 + 8800 + 40600, "quotation subtotal");
        Check(q.TaxAmount() == Fmt.R2(91400 * 0.18), "sales tax amount");
        Check(store.StageOf(q) == OrderStage.NeedsChallan, "stage: needs challan");

        var c = store.SaveChallan(store.DraftChallan(q));
        Check(c.PoNumber == "PO-7781", "challan carries PO number");
        Check(store.StageOf(q) == OrderStage.AwaitingApproval, "stage: awaiting approval");
        Throws(() => store.DraftInvoice(c), "invoice blocked before approval");
        store.SetApproved(c, true);
        Check(store.ItemByCode("CBL-25")!.Stock == 11, "approval removes stock (15 → 11)");
        Check(store.StageOf(q) == OrderStage.ReadyToInvoice, "stage: ready to invoice");

        var inv = store.DraftInvoice(store.ChallanById(c.Id)!);
        Check(inv.Lines[0].Rate == 10500, "invoice starts at quotation price");
        inv = store.SaveInvoice(inv with { Lines = inv.Lines.Select((l, i) => i == 0 ? l with { Rate = 11000 } : l).ToList() });
        Check(inv.Subtotal() == q.Subtotal() + 2000, "invoice price changed for margin");
        Check(store.StageOf(q) == OrderStage.Invoiced, "stage: invoiced");
        q = store.SaveQuotation(q with { PoNumber = "PO-7781-A" });
        Check(store.ChallanById(c.Id)!.PoNumber == "PO-7781-A" && store.InvoiceForQuotation(q.Id)!.PoNumber == "PO-7781-A", "PO change flows to challan and invoice");
        Throws(() => store.SetApproved(c, false), "cannot un-approve invoiced challan");

        var all = RangePreset.AllTime.Interval(DateTime.Now, DateTime.Now);
        var ov = store.GetOverview(all);
        Check(ov.NetSales == inv.Subtotal(), "report: net sales match invoice");
        var costAtSale = 4 * 9200.0 + 40 * 150.0;
        Check(ov.Cost == costAtSale, $"report: cost of goods uses cost at time of sale ({ov.Cost})");
        store.SaveItem(store.ItemByCode("CBL-25")! with { CostPrice = 9999 });
        Check(store.GetOverview(all).Cost == costAtSale, "report: later cost change does not alter old profit");
        Check(store.ItemsReport(all).Rows.First(r => r.Cells[0].Text == "CBL-25").Cells[3].Value == 4, "report: item by item qty sold");
        Check(store.BillsReport(all).Rows.Count == 1 && store.LineReport(all).Rows.Count == 30, "report: bills and line detail");
        var daily = store.DailyReport(all);
        Check(daily.Totals(daily.Rows)![3].Value == inv.Subtotal(), "report: day by day totals");
        Check(!daily.Csv(daily.Rows, false).Contains("Profit"), "report: Excel export hides cost when hidden");
        var rpdf = ReportPdf.Render(store.LineReport(all), store.LineReport(all).Rows, "All time", store.Settings, true);
        File.WriteAllBytes(Path.Combine(folder, "report-lines.pdf"), rpdf);
        Check(rpdf.Length > 5000, "report PDF rendered");
        Check(Fmt.Words(268450, "Rupees") == "Rupees Two Hundred Sixty-Eight Thousand Four Hundred Fifty Only", "amount in words");
        Check(Fmt.Words(1250.5, "Rupees") == "Rupees One Thousand Two Hundred Fifty and Fifty Paisa Only", "amount in words with paisa");

        foreach (var (name, doc) in new[] { ("quotation", store.Printable(q)), ("challan", store.Printable(store.ChallanById(c.Id)!)), ("invoice", store.Printable(inv)) })
        {
            var pdf = PdfDocs.Render(doc, store.Settings);
            File.WriteAllBytes(Path.Combine(folder, $"{name}.pdf"), pdf);
            Check(pdf.Length > 5000, $"{name}.pdf rendered ({pdf.Length / 1024} KB, {PdfDocs.Paginate(doc, store.Settings, false).Count} pages)");
        }
        var backup = Path.Combine(folder, "backup.sqlite");
        store.Backup(backup);
        Check(File.Exists(backup) && new FileInfo(backup).Length > 10000, "backup copy written");
        Console.WriteLine("ALL PASSED");
        return 0;
    }

    // Two computers through an in-memory stand-in for Supabase (same rules as the setup SQL).
    sealed class FakeServer
    {
        public sealed record Rec(byte[]? Data, double UpdatedAt, bool Deleted, int Seq);
        public readonly Dictionary<string, Dictionary<string, Rec>> Tables = new();
        public int Seq;
        public void Push(IEnumerable<OutgoingRow> rows)
        {
            foreach (var r in rows)
            {
                if (!Tables.TryGetValue(r.Table, out var t)) Tables[r.Table] = t = new();
                if (t.TryGetValue(r.Uid, out var old) && old.UpdatedAt > r.UpdatedAt) continue;
                t[r.Uid] = new Rec(r.Data, r.UpdatedAt, r.Deleted, ++Seq);
            }
        }
        public List<IncomingRow> Pull(int after) => Tables.SelectMany(t => t.Value.Where(x => x.Value.Seq > after)
            .Select(x => new IncomingRow(t.Key, x.Key, x.Value.Data, x.Value.UpdatedAt, x.Value.Deleted))).ToList();
    }

    public static int SyncTest(string folder)
    {
        Fresh(folder);
        using var a = new Store(Path.Combine(folder, "A"));
        using var b = new Store(Path.Combine(folder, "B"));
        var server = new FakeServer();
        var cursors = new Dictionary<Store, int>();
        void Sync(Store s)
        {
            var start = server.Seq;
            var rows = server.Pull(cursors.GetValueOrDefault(s));
            if (rows.Count > 0) { var r = s.ApplyRemote(rows); if (r.Problems.Count > 0) Console.WriteLine("      problems: " + string.Join("; ", r.Problems)); s.ReloadAfterSync(); }
            cursors[s] = start;
            var output = s.PendingChanges();
            server.Push(output);
            s.MarkPushed(output);
        }
        void SyncAll() { for (int i = 0; i < 2; i++) { Sync(a); Sync(b); } }

        a.SavePurchase(new Purchase { Number = "P-1", Supplier = "Sup", Lines = new[] { new PurchaseLine { Code = "X1", Name = "Cable", Qty = 10, Cost = 100, SalePrice = 150 }, new PurchaseLine { Code = "X2", Name = "Switch", Qty = 50, Cost = 10, SalePrice = 20 } } });
        var x1 = a.ItemByCode("X1")!;
        var q = a.SaveQuotation(a.DraftQuotation() with { CustomerName = "Customer One", PoNumber = "PO-9", Lines = new[] { new DocLine { ItemId = x1.Id, Code = "X1", Name = "Cable", Qty = 3, Rate = 150, BaseRate = 150 } } });
        var c = a.SaveChallan(a.DraftChallan(q));
        a.SetApproved(c, true);
        a.SaveInvoice(a.DraftInvoice(a.ChallanById(c.Id)!));
        Check(a.ItemByCode("X1")!.Stock == 7, "A: stock 10 − 3 delivered = 7");
        Check(a.PendingCount() > 0, $"A: changes waiting to upload ({a.PendingCount()})");
        SyncAll();
        Check(a.PendingCount() == 0, "A: nothing waiting after sync");
        Check(b.Items.Count == 2 && b.Purchases.Count == 1 && b.Quotations.Count == 1, "B: received items, purchase, quotation");
        Check(b.Challans.FirstOrDefault()?.Status == ChallanStatus.Approved && b.Invoices.Count == 1, "B: received approved challan and invoice");
        Check(b.ItemByCode("X1")!.Stock == 7, "B: same stock as A (7)");

        a.SavePurchase(new Purchase { Number = "P-2", Lines = new[] { new PurchaseLine { ItemId = x1.Id, Code = "X1", Qty = 5, Cost = 100 } } });
        var bx = b.ItemByCode("X1")!;
        b.SavePurchase(new Purchase { Number = "P-3", Lines = new[] { new PurchaseLine { ItemId = bx.Id, Code = "X1", Qty = 8, Cost = 100 } } });
        SyncAll();
        Check(a.ItemByCode("X1")!.Stock == 20 && b.ItemByCode("X1")!.Stock == 20, "simultaneous stock-in on both: 7 + 5 + 8 = 20 on both");

        a.SaveQuotation(a.Quotations[0] with { CustomerName = "Edited on A" });
        Thread.Sleep(20);
        b.SaveQuotation(b.Quotations[0] with { CustomerName = "Edited on B (later)" });
        SyncAll();
        Check(a.Quotations[0].CustomerName == "Edited on B (later)" && b.Quotations[0].CustomerName == "Edited on B (later)", "same quotation edited on both: newer edit wins");

        a.SaveItem(new Item { Code = "DUP", Name = "Made on A" });
        b.SaveItem(new Item { Code = "DUP", Name = "Made on B" });
        SyncAll();
        var ca = a.Items.Where(i => i.Code.StartsWith("DUP")).Select(i => $"{i.Code}={i.Name}").OrderBy(x => x).ToList();
        var cb = b.Items.Where(i => i.Code.StartsWith("DUP")).Select(i => $"{i.Code}={i.Name}").OrderBy(x => x).ToList();
        Check(ca.Count == 2 && ca.SequenceEqual(cb), $"same Item ID on both: kept both, same everywhere [{string.Join(", ", ca)}]");

        b.DeleteInvoice(b.Invoices[0]);
        SyncAll();
        Check(a.Invoices.Count == 0, "invoice deleted on B is deleted on A");
        b.DeleteChallan(b.Challans[0]);
        SyncAll();
        Check(a.Challans.Count == 0 && a.ItemByCode("X1")!.Stock == 23, "challan deleted on B: A gets the 3 back into stock (23)");

        a.SaveSettings(a.Settings with { CompanyName = "AFAN TRADERS (Lahore)", DefaultTaxRate = 17 });
        SyncAll();
        Check(b.Settings.CompanyName == "AFAN TRADERS (Lahore)" && b.Settings.DefaultTaxRate == 17, "settings synced to B");
        b.SaveItem(b.ItemByCode("X2")! with { Stock = 45 });
        SyncAll();
        Check(a.ItemByCode("X2")!.Stock == 45, "manual stock correction on B shows on A (45)");
        Console.WriteLine("ALL PASSED");
        return 0;
    }

    public static int LanTest(string folder)
    {
        Fresh(folder);
        var stores = new[] { "A", "B", "C-wrong-code", "D" }.Select(n => new Store(Path.Combine(folder, n))).ToArray();
        var (a, b, c, d) = (stores[0], stores[1], stores[2], stores[3]);
        var lans = stores.Select(s => new LanSync(s, new MemorySecrets(), Guid.NewGuid().ToString())).ToArray();
        var (la, lb, lc, ld) = (lans[0], lans[1], lans[2], lans[3]);
        a.SavePurchase(new Purchase { Number = "P-1", Lines = new[] { new PurchaseLine { Code = "X1", Name = "Cable", Qty = 10, Cost = 100, SalePrice = 150 } } });
        var x1 = a.ItemByCode("X1")!;
        a.SaveQuotation(a.DraftQuotation() with { CustomerName = "Customer One", PoNumber = "PO-1", Lines = new[] { new DocLine { ItemId = x1.Id, Code = "X1", Name = "Cable", Qty = 3, Rate = 150, BaseRate = 150 } } });

        foreach (var l in new[] { la, lb, ld }) l.StartForTest("afan-1234");
        lc.StartForTest("wrong-code");
        Check(lans.All(l => l.Port > 0), "all computers listening");
        void Link(LanSync x, LanSync y) => x.Connect("127.0.0.1", y.Port!.Value);
        Link(la, lb); Link(lb, la);
        Check(Wait(() => b.ItemByCode("X1")?.Stock == 10 && b.Quotations.Count == 1), "B received A's stock (10) and quotation");
        Check(lb.Peers.Any(p => p.Connected), $"B shows A as connected: {lb.StatusText}");

        var ch = b.SaveChallan(b.DraftChallan(b.Quotations[0]));
        b.SetApproved(ch, true);
        Check(Wait(() => a.Challans.FirstOrDefault()?.Status == ChallanStatus.Approved && a.ItemByCode("X1")?.Stock == 7, 3), "B approves challan → A sees it within 3 s, stock 7");

        a.SavePurchase(new Purchase { Number = "P-2", Lines = new[] { new PurchaseLine { ItemId = x1.Id, Code = "X1", Qty = 5, Cost = 100 } } });
        var bx = b.ItemByCode("X1")!;
        b.SavePurchase(new Purchase { Number = "P-3", Lines = new[] { new PurchaseLine { ItemId = bx.Id, Code = "X1", Qty = 8, Cost = 100 } } });
        Check(Wait(() => a.ItemByCode("X1")?.Stock == 20 && b.ItemByCode("X1")?.Stock == 20), "both add stock at once: 7 + 5 + 8 = 20 on both");

        a.DeletePurchase(a.Purchases.First(p => p.Number == "P-2"));
        Check(Wait(() => b.Purchases.Count == 2 && b.ItemByCode("X1")?.Stock == 15), "purchase deleted on A is gone on B, stock 15");
        a.SaveSettings(a.Settings with { CompanyName = "AFAN TRADERS – Office" });
        Check(Wait(() => b.Settings.CompanyName == "AFAN TRADERS – Office"), "settings synced");

        Link(lc, la);
        Check(Wait(() => lc.Problems.Count > 0, 3), $"wrong office code refused: {lc.Problems.FirstOrDefault() ?? "-"}");
        Check(c.Items.Count == 0 && c.Quotations.Count == 0, "computer with wrong code received nothing");

        Link(ld, lb); Link(lb, ld);
        Check(Wait(() => d.ItemByCode("X1")?.Stock == 15 && d.Quotations.Count == 1 && d.Challans.Count == 1 && d.Settings.CompanyName == "AFAN TRADERS – Office"),
              "third computer linked only to B gets A's data too");
        d.SaveItem(new Item { Code = "NEW-D", Name = "Made on D" });
        Check(Wait(() => a.ItemByCode("NEW-D") != null), "item made on D reaches A through B");
        Check(d.PendingCount() > 0, $"office changes also queued for cloud upload ({d.PendingCount()})");
        Console.WriteLine("ALL PASSED");
        foreach (var l in lans) l.Dispose();
        return 0;
    }

    /// <summary>Opens a database created by the Mac app and checks everything reads the same.</summary>
    public static int MacDb(string folder, string macDbFolder)
    {
        Directory.CreateDirectory(Fresh(folder));
        foreach (var f in Directory.GetFiles(macDbFolder, "afan-traders.sqlite*")) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
        using var s = new Store(folder);
        var expected = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(macDbFolder, "expected.json")))!;
        Check(s.Items.Count == expected["items"].GetInt32(), $"items: {s.Items.Count}");
        Check(s.Quotations.Count == expected["quotations"].GetInt32(), $"quotations: {s.Quotations.Count}");
        Check(s.Challans.Count == expected["challans"].GetInt32() && s.Invoices.Count == expected["invoices"].GetInt32(), $"challans {s.Challans.Count}, invoices {s.Invoices.Count}");
        foreach (var it in expected["stock"].EnumerateObject())
            Check(s.ItemByCode(it.Name)?.Stock == it.Value.GetDouble(), $"stock {it.Name} = {it.Value.GetDouble()}");
        var all = RangePreset.AllTime.Interval(DateTime.Now, DateTime.Now);
        Check(Math.Abs(s.GetOverview(all).NetSales - expected["net_sales"].GetDouble()) < 0.01, $"net sales {s.GetOverview(all).NetSales}");
        var inv = s.Invoices.OrderBy(i => i.Id).First();
        File.WriteAllBytes(Path.Combine(folder, "mac-invoice.pdf"), PdfDocs.Render(s.Printable(inv), s.Settings));
        Check(s.Settings.CompanyName == expected["company"].GetString(), $"settings company: {s.Settings.CompanyName}");
        // Make a change here (Windows) and check the Mac app can read it back.
        s.SaveQuotation(s.DraftQuotation() with { CustomerName = "Made on Windows", Lines = new[] { new DocLine { Code = "W1", Name = "Windows line", Qty = 2, Rate = 500 } } });
        Check(s.Quotations.Any(q => q.CustomerName == "Made on Windows"), "Windows wrote a quotation into the Mac database");
        Console.WriteLine("ALL PASSED");
        return 0;
    }

    /// <summary>Office sync between this program and a running Mac app (direct connection to its port).</summary>
    public static int MacLan(string folder, string code, int macPort)
    {
        using var s = new Store(Fresh(folder));
        using var lan = new LanSync(s, new MemorySecrets(), Guid.NewGuid().ToString());
        lan.StartForTest(code);
        Console.WriteLine($"WINDOWS-SIDE PORT {lan.Port} DEVICE {lan.DeviceId[..12]}");
        lan.Connect("127.0.0.1", macPort);
        Check(Wait(() => lan.Peers.Any(p => p.Connected), 10), $"connected to the Mac app: {lan.StatusText}");
        Check(Wait(() => s.Quotations.Count > 0 && s.Items.Count > 0, 10), $"received Mac data: {s.Items.Count} items, {s.Quotations.Count} quotations, {s.Invoices.Count} invoices");
        s.SaveItem(new Item { Code = "FROM-WIN", Name = "Item made on Windows", SalePrice = 99 });
        Console.WriteLine("MADE FROM-WIN – waiting for the Mac app to pull it");
        // Keep serving so the Mac app can pull from us.
        Thread.Sleep(int.Parse(Environment.GetEnvironmentVariable("AFAN_SERVE_SECONDS") ?? "25") * 1000);
        Console.WriteLine("DONE");
        return 0;
    }

    /// <summary>Lists the WPF resources (icons) packed into an app assembly (without loading it).</summary>
    public static int Resources(string dll)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        var section = pe.GetSectionData(dir.RelativeVirtualAddress);
        foreach (var h in md.ManifestResources)
        {
            var res = md.GetManifestResource(h);
            var name = md.GetString(res.Name);
            Console.WriteLine($"manifest: {name}");
            if (!name.EndsWith(".g.resources")) continue;
            var reader = section.GetReader((int)res.Offset, section.Length - (int)res.Offset);
            var len = reader.ReadInt32();
            var bytes = reader.ReadBytes(len);
            using var rr = new System.Resources.ResourceReader(new MemoryStream(bytes));
            var e = rr.GetEnumerator();
            while (e.MoveNext()) Console.WriteLine($"  {e.Key}");
        }
        return 0;
    }

    public static int Pdf(string folder, string[] letterheads)
    {
        Directory.CreateDirectory(folder);
        var s = new CompanySettings();
        var logo = Environment.GetEnvironmentVariable("AFAN_TEST_LOGO");
        if (logo != null) s = s with { Logo = File.ReadAllBytes(logo), Address = "Plot 7, Sundar Industrial Estate\nLahore", Phone = "042-1234567", Ntn = "1234567-8" };
        File.WriteAllBytes(Path.Combine(folder, "sample-builtin.pdf"), PdfDocs.Render(PrintableDoc.Sample(PrintableDoc.DocKind.Invoice, s), s));
        foreach (var lh in letterheads)
        {
            var s2 = s with { Letterhead = File.ReadAllBytes(lh) };
            Console.WriteLine(Letterhead.From(s2.Letterhead) == null ? $"LETTERHEAD NOT READABLE {lh}" : $"letterhead ok {Path.GetFileName(lh)}");
            File.WriteAllBytes(Path.Combine(folder, $"sample-{Path.GetExtension(lh).Trim('.')}.pdf"), PdfDocs.Render(PrintableDoc.Sample(PrintableDoc.DocKind.Quotation, s2), s2));
        }
        var many = PrintableDoc.Sample(PrintableDoc.DocKind.Challan, s) with
        {
            Lines = Enumerable.Range(1, 30).Select(i => new DocLine { Code = $"MISC-{i}", Name = $"Fitting item {i}", Details = i % 3 == 0 ? "Extra detail line" : "", Qty = i, Rate = 100 }).ToList(),
        };
        File.WriteAllBytes(Path.Combine(folder, "challan-30.pdf"), PdfDocs.Render(many, s));
        Console.WriteLine($"pages for 30-line challan: {PdfDocs.Paginate(many, s, false).Count}");
        return 0;
    }
}

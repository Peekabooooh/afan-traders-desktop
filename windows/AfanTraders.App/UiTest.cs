using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>
/// "AFAN Traders.exe --uitest &lt;folder&gt;": fills a sample database, opens every page, editor,
/// report and the PDF preview, saves a screenshot of each and writes uitest.log (PASS/FAIL).
/// </summary>
static class UiTest
{
    static string folder = "";
    static readonly List<string> log = new();
    static int failures;

    public static async Task<int> Run(string dir)
    {
        folder = dir;
        Directory.CreateDirectory(folder);
        var st = AppServices.Current.Store;
        Step("seed sample data", () => Seed(st));

        var main = new MainWindow();
        Application.Current.MainWindow = main;
        main.Show();
        await Pump();
        foreach (Section s in Enum.GetValues<Section>())
            await Shot($"page-{s}", main, () => main.Show(s));
        AppServices.Current.ShowCost = true;
        await Shot("page-Inventory-costs-shown", main, () => main.Show(Section.Inventory));
        await Shot("page-Purchases-costs-shown", main, () => main.Show(Section.Purchases));

        main.Show(Section.Reports);
        await Pump();
        var reports = FindPage<ReportsPage>(main);
        if (reports != null)
            for (int k = 0; k < 9; k++)
            {
                var idx = k;
                await Shot($"report-{k}", main, () => reports.SelectReport(idx));
            }

        var q = st.Quotations.First(x => st.StageOf(x) == OrderStage.Invoiced);
        var openQ = st.Quotations.FirstOrDefault(x => st.StageOf(x) == OrderStage.NeedsChallan) ?? q;
        await Window("editor-quotation-new", () => new QuotationWindow(st.DraftQuotation()));
        await Window("editor-quotation-open", () => new QuotationWindow(openQ));
        await Window("editor-quotation-locked", () => new QuotationWindow(q));
        await Window("editor-challan", () => new ChallanWindow(st.ChallanForQuotation(q.Id)!));
        await Window("editor-challan-new", () => new ChallanWindow(st.DraftChallan(openQ)));
        await Window("editor-invoice", () => new InvoiceWindow(st.InvoiceForQuotation(q.Id)!));
        await Window("editor-item", () => new ItemWindow(st.Items[0]));
        await Window("editor-item-new", () => new ItemWindow(new Item()));
        await Window("editor-purchase-new", () => new PurchaseWindow(new Purchase()));
        await Window("editor-purchase-open", () => new PurchaseWindow(st.Purchases[0]));

        foreach (var (name, doc) in new[] { ("quotation", st.Printable(q)), ("challan", st.Printable(st.ChallanForQuotation(q.Id)!)), ("invoice", st.Printable(st.InvoiceForQuotation(q.Id)!)) })
        {
            await Window($"pdf-preview-{name}", () => new PdfPreviewWindow(PdfDocs.Render(doc, st.Settings), name, name + ".pdf"), wait: 2500);
            await StepAsync($"windows pdf engine renders {name}", async () =>
            {
                var pages = await PdfService.RenderPages(PdfDocs.Render(doc, st.Settings), 96);
                if (pages.Count == 0) throw new Exception("no pages");
                Save(pages[0], $"pdf-page-{name}");
            });
        }
        Step("picture compression (JPEG)", () =>
        {
            var png = Render(main, 300, 200);
            var jpg = Images.Compress(png) ?? throw new Exception("compress failed");
            if (jpg.Length == 0 || Images.Load(jpg) == null) throw new Exception("unreadable");
        });
        Step("secret store (DPAPI)", () =>
        {
            AppServices.Current.Secrets.Set("uitest", "ok-123");
            if (AppServices.Current.Secrets.Get("uitest") != "ok-123") throw new Exception("value not read back");
            AppServices.Current.Secrets.Delete("uitest");
        });
        await StepAsync("office sync starts (listens and searches the network)", async () =>
        {
            AppServices.Current.Lan.StartForTest("uitest-code");
            await Task.Delay(1500);
            if (AppServices.Current.Lan.Port is not > 0) throw new Exception("not listening");
            AppServices.Current.Lan.TurnOff();
        });

        log.Add(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        File.WriteAllLines(Path.Combine(folder, "uitest.log"), log);
        main.Close();
        return failures == 0 ? 0 : 1;
    }

    static T? FindPage<T>(MainWindow main) where T : Page
    {
        if (main.Content is System.Windows.Controls.Grid g)
            foreach (var c in g.Children)
                if (c is System.Windows.Controls.ContentControl cc && cc.Content is T t) return t;
        return null;
    }

    static void Step(string what, Action a)
    {
        try { a(); log.Add($"PASS  {what}"); }
        catch (Exception e) { failures++; log.Add($"FAIL  {what}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
    }

    static async Task StepAsync(string what, Func<Task> a)
    {
        try { await a(); log.Add($"PASS  {what}"); }
        catch (Exception e) { failures++; log.Add($"FAIL  {what}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
    }

    static async Task Pump(int ms = 300)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task Shot(string name, Window w, Action action)
    {
        await StepAsync($"show {name}", async () =>
        {
            action();
            await Pump();
            Save(w, name);
        });
    }

    static async Task Window(string name, Func<Window> make, int wait = 600)
    {
        await StepAsync($"open {name}", async () =>
        {
            var w = make();
            w.Owner = Application.Current.MainWindow;
            w.Show();
            await Pump(wait);
            Save(w, name);
            w.Close();
        });
    }

    static byte[] Render(Window w, int width, int height)
    {
        var rt = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rt.Render((Visual)w.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rt));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void Save(Window w, string name) =>
        File.WriteAllBytes(Path.Combine(folder, name + ".png"), Render(w, Math.Max(200, (int)w.ActualWidth), Math.Max(200, (int)w.ActualHeight)));

    static void Save(BitmapSource img, string name)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var f = File.Create(Path.Combine(folder, name + ".png"));
        enc.Save(f);
    }

    static void Seed(Store st)
    {
        st.SavePurchase(new Purchase
        {
            Number = "PINV-501", Supplier = "Pak Cables", Lines = new[]
            {
                new PurchaseLine { Code = "CBL-25", Name = "Copper Cable 2.5mm", Details = "90m coil", Unit = "Coil", Qty = 40, Cost = 9000, SalePrice = 10500 },
                new PurchaseLine { Code = "SW-1G", Name = "Switch 1 Gang", Unit = "Pcs", Qty = 500, Cost = 150, SalePrice = 220 },
                new PurchaseLine { Code = "BRK-32", Name = "Circuit Breaker 32A", Unit = "Pcs", Qty = 60, Cost = 1100, SalePrice = 1450 },
            },
        });
        var items = st.Items.ToList();
        string[] customers = { "Nishat Mills", "Packages Ltd", "Engro Foods", "Lucky Cement" };
        for (int d = 10; d >= 1; d--)
        {
            var date = DateTime.Today.AddDays(-d * 2);
            var q = st.SaveQuotation(st.DraftQuotation() with
            {
                Date = date, CustomerName = customers[d % customers.Length], CustomerAddress = "Industrial Area, Lahore", PoNumber = $"PO-{3000 + d}",
                Lines = items.Select((it, i) => new DocLine { ItemId = it.Id, Code = it.Code, Name = it.Name, Details = it.Details, Unit = it.Unit, Qty = 1 + (d + i) % 4, Rate = it.SalePrice, BaseRate = it.SalePrice }).ToList(),
            });
            if (d <= 2) continue;   // leave some open orders
            var c = st.SaveChallan(st.DraftChallan(q) with { Date = date, VehicleNo = "LES-1234" });
            if (d == 3) continue;   // one challan pending
            st.SetApproved(c, true);
            if (d == 4) continue;   // one ready to invoice
            var inv = st.DraftInvoice(st.ChallanById(c.Id)!);
            st.SaveInvoice(inv with { Date = date, Lines = inv.Lines.Select(l => l with { Rate = l.Rate * 1.05 }).ToList() });
        }
    }
}

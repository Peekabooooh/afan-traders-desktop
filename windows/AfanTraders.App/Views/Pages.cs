using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

static class Tables
{
    /// <summary>The order tracker: every quotation with its challan and invoice status.</summary>
    public static DataGrid Quotations()
    {
        var g = Grids.Make();
        var st = AppServices.Current.Store;
        g.Add<Quotation>("Quotation No.", 125, q => q.Number, nameof(Quotation.Number), bold: true);
        g.Add<Quotation>("Date", 105, q => Fmt.Date(q.Date), nameof(Quotation.Date));
        g.Add<Quotation>("PO No.", 110, q => q.PoNumber.Length == 0 ? "—" : q.PoNumber, nameof(Quotation.PoNumber));
        g.Add<Quotation>("Customer", 0, q => q.CustomerName, nameof(Quotation.CustomerName));
        g.Add<Quotation>("Total", 120, q => Fmt.Money(q.GrandTotal()), right: true);
        g.AddCell<Quotation>("Delivery Challan", 175, q => st.ChallanForQuotation(q.Id) is { } c
            ? Ui.Row(Ui.Text(c.Number + "  ", 13), Ui.Pill(c.Status == ChallanStatus.Approved ? "Approved" : "Pending", c.Status == ChallanStatus.Approved ? Ui.Green : Ui.Orange))
            : Ui.Text("Not created", 13, null, Ui.Muted));
        g.AddCell<Quotation>("Invoice", 135, q => st.InvoiceForQuotation(q.Id) is { } i ? Ui.Text("✔ " + i.Number, 13, null, Ui.Green) : Ui.Text("Not created", 13, null, Ui.Muted));
        g.AddCell<Quotation>("Next Step", 140, q => PageParts.NextStepButton(q));
        g.OnOpen<Quotation>(q => Nav.Open(new QuotationWindow(q)));
        g.Menu<Quotation>(q =>
        {
            var items = new List<(string, Action)>
            {
                ("Open Quotation", () => Nav.Open(new QuotationWindow(q))),
                ("Preview / Print Quotation", () => Nav.Preview(st.Printable(q))),
            };
            if (st.ChallanForQuotation(q.Id) is { } c) items.Add(($"Open Delivery Challan {c.Number}", () => Nav.Open(new ChallanWindow(c))));
            else items.Add(("Create Delivery Challan", () => Nav.Open(new ChallanWindow(st.DraftChallan(q)))));
            if (st.InvoiceForQuotation(q.Id) is { } inv) items.Add(($"Open Invoice {inv.Number}", () => Nav.Open(new InvoiceWindow(inv))));
            items.Add(("-", () => { }));
            items.Add(("Duplicate as New Quotation", () => Nav.Open(new QuotationWindow(q with
            {
                Id = 0, Number = st.NewQuotationNumber(), Date = DateTime.Now, PoNumber = "", Lines = q.Lines.Select(l => l with { Key = Guid.NewGuid() }).ToList(),
            }))));
            return items;
        });
        return g;
    }
}

sealed class DashboardPage : Page
{
    readonly WrapPanel cards = new();
    readonly DataGrid open = Tables.Quotations();
    readonly TextBlock company = Ui.Text("", 30, FontWeights.Bold, Brushes.White);
    readonly TextBlock summary = Ui.Text("", 13.5, null, Ui.Hex("#C9CEF9"));
    readonly Squircle emptyOpen = Ui.Card(Ui.Text("Nothing pending. All quotations are invoiced.", 14, null, Ui.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center), 30);

    public DashboardPage()
    {
        // Hero: company name on a deep indigo squircle, with the two main actions.
        var actions = Ui.Row(Ui.Btn("Add Items / Stock", Nav.NewPurchase, icon: "stockin", look: Look.Glass), Ui.Btn("New Quotation", Nav.NewQuotation, icon: "quotations", look: Look.Light));
        actions.VerticalAlignment = VerticalAlignment.Center;
        var heroDock = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        heroDock.Children.Add(actions);
        heroDock.Children.Add(Ui.Col(4, Ui.Text(Greeting() + "  ·  " + DateTime.Now.ToString("dddd, d MMMM yyyy"), 13, FontWeights.Medium, Ui.Hex("#AEB5F5")), company, summary));
        var hero = new Squircle
        {
            Child = heroDock, Padding = new Thickness(30, 26, 30, 26), CornerRadius = 24,
            Background = Theme.Gradient((Color)ColorConverter.ConvertFromString("#1A2152"), (Color)ColorConverter.ConvertFromString("#4337B8")),
            Shadow = 30, ShadowDepth = 8, ShadowOpacity = 0.22, ShadowColor = (Color)ColorConverter.ConvertFromString("#2B2F8F"),
        };

        var openHead = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var all = Ui.Btn("See all quotations", () => { Nav.Main.QuotationFilter = null; Nav.Go(Section.Quotations); }, look: Look.Ghost).Also(b => b.Foreground = Ui.Blue);
        DockPanel.SetDock(all, Dock.Right);
        openHead.Children.Add(all);
        openHead.Children.Add(Ui.Row(Ui.Heading("Open Orders", 18), Ui.Text("    Quotations that are not invoiced yet", 13, null, Ui.Muted)));
        open.MinHeight = 300;

        var body = new DockPanel { Margin = new Thickness(28, 24, 28, 24) };
        var top = Ui.Col(20, hero, cards, openHead);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(new Grid().Also(gr => { gr.Children.Add(Ui.TableCard(open)); gr.Children.Add(emptyOpen); }));
        Content = body;
    }

    static string Greeting() => DateTime.Now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    public override void Refresh()
    {
        company.Text = Store.Settings.CompanyName;
        int Count(OrderStage s) => Store.Quotations.Count(q => Store.StageOf(q) == s);
        var month = Store.Invoices.Where(i => i.Date.Year == DateTime.Now.Year && i.Date.Month == DateTime.Now.Month).Sum(i => i.GrandTotal());
        var pending = Store.Quotations.Where(q => Store.StageOf(q) != OrderStage.Invoiced).ToList();
        summary.Text = $"{pending.Count} open order{(pending.Count == 1 ? "" : "s")}   ·   {Fmt.Money(month, Store.Settings.Currency)} invoiced this month   ·   {Store.Items.Count} items in inventory";
        cards.Children.Clear();
        void Card(string title, string value, string icon, Brush color, Action click)
        {
            var arrow = Ui.Glyph("\uE76C", 11, Ui.Muted);
            var head = new DockPanel();
            DockPanel.SetDock(arrow, Dock.Right);
            head.Children.Add(arrow);
            head.Children.Add(Ui.IconTile(icon, color, 50, 32).Also(t => t.HorizontalAlignment = HorizontalAlignment.Left));
            var c = Ui.Card(Ui.Col(14, head, Ui.Col(2, Ui.Text(value, 26, FontWeights.Bold, Ui.Navy), Ui.Text(title, 13, FontWeights.Medium, Ui.Muted))), 18);
            c.Width = 236; c.Margin = new Thickness(0, 0, 16, 16); c.Cursor = System.Windows.Input.Cursors.Hand;
            c.MouseEnter += (_, _) => { c.Shadow = 28; c.ShadowDepth = 8; c.ShadowOpacity = 0.12; c.BorderBrush = Ui.Tint(color, 90); };
            c.MouseLeave += (_, _) => { c.Shadow = 18; c.ShadowDepth = 3; c.ShadowOpacity = 0.06; c.BorderBrush = Ui.Line; };
            c.MouseLeftButtonUp += (_, _) => click();
            cards.Children.Add(c);
        }
        void Go(OrderStage? s) { Nav.Main.QuotationFilter = s; Nav.Go(Section.Quotations); }
        Card("Needs Delivery Challan", $"{Count(OrderStage.NeedsChallan)}", "challans", Ui.Blue, () => Go(OrderStage.NeedsChallan));
        Card("Challan Pending Approval", $"{Count(OrderStage.AwaitingApproval)}", "pending", Ui.Orange, () => Go(OrderStage.AwaitingApproval));
        Card("Ready to Invoice", $"{Count(OrderStage.ReadyToInvoice)}", "ready", Ui.Green, () => Go(OrderStage.ReadyToInvoice));
        Card("Invoiced This Month", Fmt.Money(month, Store.Settings.Currency), "money", Ui.Hex("#7357F6"), () => Nav.Go(Section.Invoices));
        Card("Items Out of Stock", $"{Store.Items.Count(i => i.Stock <= 0)}", "warning", Ui.Red, () => Nav.Go(Section.Inventory));
        open.ItemsSource = pending;
        open.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyOpen.Visibility = pending.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class QuotationsPage : Page
{
    readonly DataGrid grid = Tables.Quotations();
    readonly StackPanel filters = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 28, 14) };
    readonly TextBlock count = Ui.Text("", 13, null, Ui.Muted);
    readonly Squircle empty;
    string search = "";

    public QuotationsPage()
    {
        empty = PageParts.Empty("quotations", "No quotations yet", "Create your first quotation from items in your inventory.", Ui.Btn("New Quotation", Nav.NewQuotation, primary: true));
        var top = Ui.Col(0, PageParts.TitleBar("Quotations", PageParts.Search("Search quotation No., PO No. or customer", s => { search = s; Refresh(); }),
                                              Ui.Btn("New Quotation", Nav.NewQuotation, primary: true, icon: "quotations")), filters);
        Content = PageParts.Layout(top, new Grid().Also(g => { g.Children.Add(Ui.TableCard(grid)); g.Children.Add(empty); }));
    }

    void BuildFilters()
    {
        filters.Children.Clear();
        void F(string label, OrderStage? s)
        {
            var on = Nav.Main.QuotationFilter == s;
            var b = new ToggleButton { Content = label, IsChecked = on, Style = Theme.Chip, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => { Nav.Main.QuotationFilter = s; Refresh(); };
            filters.Children.Add(b);
        }
        F("All", null);
        foreach (var s in Enum.GetValues<OrderStage>()) F(s.Short(), s);
        filters.Children.Add(count.Also(c => c.Margin = new Thickness(16, 0, 0, 0)));
    }

    public override void Refresh()
    {
        BuildFilters();
        var q = search.T();
        var rows = Store.Quotations.Where(x => (Nav.Main.QuotationFilter is not { } f || Store.StageOf(x) == f)
            && (q.Length == 0 || x.Number.Contains(q, StringComparison.OrdinalIgnoreCase) || x.PoNumber.Contains(q, StringComparison.OrdinalIgnoreCase) || x.CustomerName.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        grid.ItemsSource = rows;
        count.Text = $"{rows.Count} quotation{(rows.Count == 1 ? "" : "s")}";
        empty.Visibility = Store.Quotations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Visibility = Store.Quotations.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class ChallansPage : Page
{
    readonly DataGrid grid = Grids.Make();
    readonly Squircle empty = PageParts.Empty("challans", "No delivery challans yet", "Open a quotation and click “Create Delivery Challan”.");
    string search = "";

    public ChallansPage()
    {
        grid.Add<Challan>("DC No.", 120, c => c.Number, nameof(Challan.Number), bold: true);
        grid.Add<Challan>("Date", 105, c => Fmt.Date(c.Date), nameof(Challan.Date));
        grid.Add<Challan>("PO No.", 110, c => c.PoNumber.Length == 0 ? "—" : c.PoNumber, nameof(Challan.PoNumber));
        grid.Add<Challan>("Quotation", 120, c => Store.QuotationById(c.QuotationId)?.Number ?? "—", color: _ => Ui.Muted);
        grid.Add<Challan>("Customer", 0, c => c.CustomerName, nameof(Challan.CustomerName));
        grid.Add<Challan>("Vehicle", 100, c => c.VehicleNo.Length == 0 ? "—" : c.VehicleNo);
        grid.AddCell<Challan>("Status", 150, c => Ui.Pill(c.Status == ChallanStatus.Approved ? "✔ Approved" : "⏳ Pending approval", c.Status == ChallanStatus.Approved ? Ui.Green : Ui.Orange));
        grid.AddCell<Challan>("Invoice", 150, c => Store.InvoiceForChallan(c.Id) is { } i ? Ui.Text(i.Number, 13, null, Ui.Green)
            : c.Status == ChallanStatus.Approved ? Ui.Btn("Create Invoice", () => Nav.Open(new InvoiceWindow(Store.DraftInvoice(c))), primary: true, tint: Ui.Green).Also(b => { b.Padding = new Thickness(12, 3, 12, 3); b.MinHeight = 30; b.Margin = new Thickness(0); })
            : Ui.Text("—", 13, null, Ui.Muted));
        grid.AddCell<Challan>("", 56, c => Ui.IconBtn("printer", () => Nav.Preview(Store.Printable(c)), "Preview / Print"));
        grid.OnOpen<Challan>(c => Nav.Open(new ChallanWindow(c)));
        grid.Menu<Challan>(c => new List<(string, Action)>
        {
            ("Open Delivery Challan", () => Nav.Open(new ChallanWindow(c))),
            ("Preview / Print", () => Nav.Preview(Store.Printable(c))),
            ("Open Quotation", () => { if (Store.QuotationById(c.QuotationId) is { } q) Nav.Open(new QuotationWindow(q)); }),
        });
        var top = PageParts.TitleBar("Delivery Challans", PageParts.Search("Search DC No., PO No., customer or vehicle", s => { search = s; Refresh(); }));
        Content = PageParts.Layout(top, new Grid().Also(g => { g.Children.Add(Ui.TableCard(grid)); g.Children.Add(empty); }));
    }

    public override void Refresh()
    {
        var q = search.T();
        grid.ItemsSource = Store.Challans.Where(c => q.Length == 0 || c.Number.Contains(q, StringComparison.OrdinalIgnoreCase) || c.PoNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.CustomerName.Contains(q, StringComparison.OrdinalIgnoreCase) || c.VehicleNo.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        empty.Visibility = Store.Challans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Visibility = Store.Challans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class InvoicesPage : Page
{
    readonly DataGrid grid = Grids.Make();
    readonly TextBlock totals = Ui.Text("", 13, FontWeights.SemiBold, Theme.Ink2);
    readonly Squircle empty = PageParts.Empty("invoices", "No invoices yet", "Approve a delivery challan, then click “Create Invoice”.");
    string search = "";

    public InvoicesPage()
    {
        grid.Add<Invoice>("Invoice No.", 125, i => i.Number, nameof(Invoice.Number), bold: true);
        grid.Add<Invoice>("Date", 105, i => Fmt.Date(i.Date), nameof(Invoice.Date));
        grid.Add<Invoice>("PO No.", 110, i => i.PoNumber.Length == 0 ? "—" : i.PoNumber, nameof(Invoice.PoNumber));
        grid.Add<Invoice>("Quotation", 115, i => Store.QuotationById(i.QuotationId)?.Number ?? "—", color: _ => Ui.Muted);
        grid.Add<Invoice>("DC No.", 115, i => Store.ChallanById(i.ChallanId)?.Number ?? "—", color: _ => Ui.Muted);
        grid.Add<Invoice>("Customer", 0, i => i.CustomerName, nameof(Invoice.CustomerName));
        grid.Add<Invoice>("Subtotal", 115, i => Fmt.Money(i.Subtotal()), right: true);
        grid.Add<Invoice>("Sales Tax", 105, i => Fmt.Money(i.TaxAmount()), right: true);
        grid.Add<Invoice>("Total", 125, i => Fmt.Money(i.GrandTotal()), right: true, bold: true);
        grid.AddCell<Invoice>("", 56, i => Ui.IconBtn("printer", () => Nav.Preview(Store.Printable(i)), "Preview / Print"));
        grid.OnOpen<Invoice>(i => Nav.Open(new InvoiceWindow(i)));
        grid.Menu<Invoice>(i => new List<(string, Action)>
        {
            ("Open Invoice", () => Nav.Open(new InvoiceWindow(i))),
            ("Preview / Print", () => Nav.Preview(Store.Printable(i))),
            ("Open Quotation", () => { if (Store.QuotationById(i.QuotationId) is { } q) Nav.Open(new QuotationWindow(q)); }),
            ("Open Delivery Challan", () => { if (Store.ChallanById(i.ChallanId) is { } c) Nav.Open(new ChallanWindow(c)); }),
        });
        var top = PageParts.TitleBar("Invoices", PageParts.Search("Search invoice No., PO No. or customer", s => { search = s; Refresh(); }));
        Content = PageParts.Layout(top, new Grid().Also(g => { g.Children.Add(Ui.TableCard(grid)); g.Children.Add(empty); }), new Border { Child = totals, Padding = new Thickness(30, 12, 30, 16) });
    }

    public override void Refresh()
    {
        var q = search.T();
        var rows = Store.Invoices.Where(i => q.Length == 0 || i.Number.Contains(q, StringComparison.OrdinalIgnoreCase) || i.PoNumber.Contains(q, StringComparison.OrdinalIgnoreCase) || i.CustomerName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        grid.ItemsSource = rows;
        var cur = Store.Settings.Currency;
        totals.Text = $"{rows.Count} invoice{(rows.Count == 1 ? "" : "s")}      Sales tax: {Fmt.Money(rows.Sum(i => i.TaxAmount()), cur)}      Total: {Fmt.Money(rows.Sum(i => i.GrandTotal()), cur)}";
        empty.Visibility = Store.Invoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Visibility = Store.Invoices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class InventoryPage : Page
{
    readonly DataGrid grid = Grids.Make();
    readonly TextBlock footer = Ui.Text("", 13, FontWeights.Medium, Theme.Ink2);
    readonly Squircle empty = PageParts.Empty("inventory", "Inventory is empty", "Enter a purchase invoice number and add all its items in one go.", Ui.Btn("Add Items / Stock", Nav.NewPurchase, primary: true));
    string search = "";

    public InventoryPage()
    {
        grid.AddCell<Item>("", 50, i => Ui.Thumb(i.Image, 32));
        grid.Add<Item>("Item ID", 130, i => i.Code, nameof(Item.Code), bold: true);
        grid.Add<Item>("Name", 0, i => i.Name, nameof(Item.Name));
        grid.Add<Item>("Description", 220, i => i.Details, color: _ => Ui.Muted);
        grid.Add<Item>("Unit", 60, i => i.Unit, color: _ => Ui.Muted);
        grid.Add<Item>("Last Purchase Inv.", 140, i => Store.LastPurchase(i)?.Number ?? "—", color: _ => Ui.Muted);
        grid.AddCell<Item>("In Stock", 95, i => i.Stock <= 0 ? Ui.Pill(Fmt.Qty(i.Stock), Ui.Red) : Ui.Text(Fmt.Qty(i.Stock), 13).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        grid.Add<Item>("Sale Price", 115, i => Fmt.Money(i.SalePrice), nameof(Item.SalePrice), right: true);
        grid.Add<Item>("Cost Price", 115, i => S.ShowCost ? Fmt.Money(i.CostPrice) : "••••••", right: true, color: _ => S.ShowCost ? null : Ui.Muted);
        grid.OnOpen<Item>(i => Nav.Open(new ItemWindow(i)));
        grid.Menu<Item>(i => new List<(string, Action)>
        {
            ("Edit Item", () => Nav.Open(new ItemWindow(i))),
            ("Add Stock (New Purchase Invoice)", () =>
            {
                S.ShowCost = true;
                Nav.Open(new PurchaseWindow(new Purchase { Lines = new[] { new PurchaseLine { ItemId = i.Id, Code = i.Code, Name = i.Name, Details = i.Details, Unit = i.Unit, Qty = 1, Cost = i.CostPrice, SalePrice = i.SalePrice } } }));
            }),
        });
        var top = PageParts.TitleBar("Inventory", PageParts.Search("Search Item ID, name or description", s => { search = s; Refresh(); }),
            Ui.Btn("New Single Item", () => Nav.Open(new ItemWindow(new Item()))),
            Ui.Btn("Add Items / Stock", Nav.NewPurchase, primary: true, icon: "stockin"));
        Content = PageParts.Layout(top, new Grid().Also(g => { g.Children.Add(Ui.TableCard(grid)); g.Children.Add(empty); }), new Border { Child = footer, Padding = new Thickness(30, 12, 30, 16) });
    }

    public override void Refresh()
    {
        var q = search.T();
        var rows = Store.Items.Where(i => q.Length == 0 || i.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Details.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        grid.ItemsSource = rows;
        var cur = Store.Settings.Currency;
        var outOf = rows.Count(i => i.Stock <= 0);
        footer.Text = $"{rows.Count} item{(rows.Count == 1 ? "" : "s")}" + (outOf > 0 ? $"    ⚠ {outOf} out of stock" : "")
            + $"      Stock value (sale): {Fmt.Money(rows.Sum(i => Math.Max(0, i.Stock) * i.SalePrice), cur)}"
            + (S.ShowCost ? $"      Stock value (cost): {Fmt.Money(rows.Sum(i => Math.Max(0, i.Stock) * i.CostPrice), cur)}" : "");
        empty.Visibility = Store.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Visibility = Store.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

sealed class PurchasesPage : Page
{
    readonly DataGrid grid = Grids.Make();
    readonly Grid holder = new();
    readonly Squircle locked, empty;

    public PurchasesPage()
    {
        grid.Add<Purchase>("Date", 110, p => Fmt.Date(p.Date), nameof(Purchase.Date));
        grid.Add<Purchase>("Invoice No.", 160, p => p.Number.Length == 0 ? "—" : p.Number, nameof(Purchase.Number), bold: true);
        grid.Add<Purchase>("Supplier", 240, p => p.Supplier.Length == 0 ? "—" : p.Supplier, nameof(Purchase.Supplier));
        grid.Add<Purchase>("Items", 0, p => string.Join(", ", p.Lines.Select(l => l.Code)), color: _ => Ui.Muted);
        grid.Add<Purchase>("Total Cost", 140, p => Fmt.Money(p.Total), right: true, bold: true);
        grid.OnOpen<Purchase>(p => Nav.Open(new PurchaseWindow(p)));
        locked = PageParts.Empty("eye", "Purchases show cost prices", "Cost prices are hidden right now. Show them to view or enter purchases.", Ui.Btn("Show Cost Prices", () => S.ShowCost = true, primary: true));
        empty = PageParts.Empty("purchases", "No purchases yet", "Enter a supplier's purchase invoice to add items to stock.", Ui.Btn("Add Items / Stock", Nav.NewPurchase, primary: true));
        holder.Children.Add(Ui.TableCard(grid)); holder.Children.Add(locked); holder.Children.Add(empty);
        Content = PageParts.Layout(PageParts.TitleBar("Purchase Invoices", null, Ui.Btn("Add Items / Stock", Nav.NewPurchase, primary: true, icon: "stockin")), holder);
    }

    public override void Refresh()
    {
        grid.ItemsSource = Store.Purchases;
        locked.Visibility = S.ShowCost ? Visibility.Collapsed : Visibility.Visible;
        empty.Visibility = S.ShowCost && Store.Purchases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Visibility = S.ShowCost && Store.Purchases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

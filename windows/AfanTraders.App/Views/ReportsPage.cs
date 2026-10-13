using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AfanTraders.Core;

namespace AfanTraders.App;

sealed class ReportsPage : Page
{
    enum Kind { Overview, Daily, Bills, Lines, Items, Customers, Orders, Purchases, Stock }
    static readonly (Kind K, string Label, string Icon)[] Tabs =
    {
        (Kind.Overview, "Overview", "reports"), (Kind.Daily, "Day by Day", "pending"), (Kind.Bills, "Bill by Bill", "invoices"),
        (Kind.Lines, "Line Detail", "quotations"), (Kind.Items, "Item by Item", "inventory"), (Kind.Customers, "Customers", "customer"),
        (Kind.Orders, "Orders", "challans"), (Kind.Purchases, "Purchases", "purchases"), (Kind.Stock, "Stock Value", "money"),
    };

    sealed class RRow
    {
        public required IReadOnlyList<ReportCell> Cells { get; init; }
        public bool IsTotal { get; init; }
        public OpenTarget? Open { get; init; }
    }

    Kind kind = Kind.Overview;
    RangePreset preset = RangePreset.ThisMonth;
    DateTime from = DateTime.Today.AddDays(-30), to = DateTime.Today;
    string search = "";
    int? sortColumn;
    bool ascending;
    readonly WrapPanel tabBar = new() { Margin = new Thickness(28, 0, 28, 8) };
    readonly StackPanel filterBar = new() { Orientation = Orientation.Horizontal };
    readonly Squircle costBanner;
    readonly ContentControl body = new();
    readonly TextBlock periodText = Ui.Text("", 13, null, Ui.Muted);
    readonly StackPanel tableTools = new() { Orientation = Orientation.Horizontal };

    (DateTime, DateTime) Range => preset.Interval(from, to);
    string PeriodLabel => preset == RangePreset.AllTime ? "All time"
        : Fmt.Date(Range.Item1) == Fmt.Date(Range.Item2) ? Fmt.Date(Range.Item1) : $"{Fmt.Date(Range.Item1)} to {Fmt.Date(Range.Item2)}";

    public ReportsPage()
    {
        var presetBox = new ComboBox { Width = 180, MinHeight = 34 };
        foreach (var p in Enum.GetValues<RangePreset>()) presetBox.Items.Add(new ComboBoxItem { Content = p.Title(), Tag = p });
        presetBox.SelectedIndex = (int)preset;
        var fromPick = Ui.Date(from, v => { from = v; Refresh(); });
        var toPick = Ui.Date(to, v => { to = v; Refresh(); });
        var custom = Ui.Row(Ui.Text("  From ", 13), fromPick, Ui.Text("  To ", 13), toPick);
        custom.Visibility = Visibility.Collapsed;
        presetBox.SelectionChanged += (_, _) =>
        {
            if (presetBox.SelectedItem is ComboBoxItem { Tag: RangePreset p }) { preset = p; custom.Visibility = p == RangePreset.Custom ? Visibility.Visible : Visibility.Collapsed; Refresh(); }
        };
        var searchBox = PageParts.Search("Filter rows (customer, item, number…)", s => { search = s; RefreshBody(); }).Also(t => t.Width = 240);
        tableTools.Children.Add(searchBox);
        tableTools.Children.Add(Ui.Btn("Export to Excel", ExportCsv));
        tableTools.Children.Add(Ui.Btn("Print / PDF", PrintReport, icon: "printer"));
        filterBar.Children.Add(Ui.Text("Period   ", 13, FontWeights.Medium, Theme.Ink2));
        filterBar.Children.Add(presetBox);
        filterBar.Children.Add(custom);
        filterBar.Children.Add(periodText.Also(t => t.Margin = new Thickness(12, 0, 0, 0)));
        var filterDock = new DockPanel { Margin = new Thickness(28, 4, 28, 14) };
        DockPanel.SetDock(tableTools, Dock.Right);
        filterDock.Children.Add(tableTools);
        filterDock.Children.Add(filterBar);

        costBanner = Ui.Note(Ui.Row(Ui.Icon("eye", 18), Ui.Text("   Cost and profit are hidden.   ", 13, FontWeights.Medium, Theme.Ink2), Ui.Btn("Show Cost & Profit", () => S.ShowCost = true)), Ui.Blue)
            .Also(n => { n.Margin = new Thickness(28, 0, 28, 14); n.Padding = new Thickness(14, 6, 8, 6); });
        var top = Ui.Col(0, PageParts.TitleBar("Reports", null), tabBar, filterDock, costBanner);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new Border { Child = body, Margin = new Thickness(28, 0, 28, 24) });
        Content = dock;
    }

    /// <summary>Used by the screen test.</summary>
    public void SelectReport(int index) { kind = (Kind)index; sortColumn = null; search = ""; Refresh(); }

    public override void Refresh()
    {
        tabBar.Children.Clear();
        foreach (var (k, label, icon) in Tabs)
        {
            var b = new ToggleButton { IsChecked = k == kind, Style = Theme.Chip, Padding = new Thickness(10, 5, 14, 5), Margin = new Thickness(0, 0, 8, 8), Content = Ui.Row(Ui.Icon(icon, 20), Ui.Text("  " + label, 13)) };
            b.Click += (_, _) => { kind = k; sortColumn = null; search = ""; Refresh(); };
            tabBar.Children.Add(b);
        }
        periodText.Text = preset == RangePreset.Custom ? "" : PeriodLabel;
        tableTools.Visibility = kind == Kind.Overview ? Visibility.Collapsed : Visibility.Visible;
        costBanner.Visibility = S.ShowCost ? Visibility.Collapsed : Visibility.Visible;
        RefreshBody();
    }

    ReportTable? Table() => kind switch
    {
        Kind.Daily => Store.DailyReport(Range), Kind.Bills => Store.BillsReport(Range), Kind.Lines => Store.LineReport(Range),
        Kind.Items => Store.ItemsReport(Range), Kind.Customers => Store.CustomersReport(Range), Kind.Orders => Store.OrdersReport(Range),
        Kind.Purchases => Store.PurchasesReport(Range), Kind.Stock => Store.StockReport(Range), _ => null,
    };

    void RefreshBody()
    {
        if (Table() is not { } t) { body.Content = Overview(); return; }
        var rows = t.FilteredSorted(search, sortColumn, ascending);
        if (rows.Count == 0) { body.Content = PageParts.Empty("reports", "Nothing in this period", "Try a wider date range, such as This Year or All Time."); return; }
        var grid = Grids.Make();
        grid.FrozenColumnCount = 1;
        for (int i = 0; i < t.Columns.Count; i++)
        {
            var col = t.Columns[i];
            var idx = i;
            grid.Columns.Add(new CodeColumn<RRow>(col.Sensitive ? $"👁 {col.Title}" : col.Title, col.Width, r =>
            {
                var cell = r.Cells[idx];
                var masked = col.Sensitive && !S.ShowCost && cell.Text.Length > 0;
                var color = masked ? Ui.Muted : col.Sensitive && cell.Value < 0 && col.Kind is CellKind.Money or CellKind.Percent ? Ui.Red : null;
                var tb = Ui.Text(masked ? "••••" : cell.Text, 13, r.IsTotal ? FontWeights.Bold : FontWeights.Normal, color);
                tb.Margin = new Thickness(6, 0, 6, 0);
                tb.HorizontalAlignment = col.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                return tb;
            }, sortProperty: "Cells") { HeaderStyle = col.Numeric ? Theme.HeaderRight : null });
        }
        // We sort ourselves (by the real numbers) and keep the totals row on top.
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            var i = grid.Columns.IndexOf(e.Column);
            if (sortColumn == i) ascending = !ascending; else { sortColumn = i; ascending = !t.Columns[i].Numeric; }
            RefreshBody();
        };
        var list = new List<RRow>();
        if (t.Totals(rows) is { } tot) list.Add(new RRow { Cells = tot, IsTotal = true });
        list.AddRange(rows.Select(r => new RRow { Cells = r.Cells, Open = r.Open }));
        grid.ItemsSource = list;
        grid.LoadingRow += (_, e) => { if (e.Row.Item is RRow { IsTotal: true }) e.Row.Background = Theme.AccentSoft; };
        grid.OnOpen<RRow>(r => OpenTarget(r.Open));
        body.Content = Ui.TableCard(grid);
    }

    void OpenTarget(OpenTarget? o)
    {
        switch (o)
        {
            case { Kind: "invoice" } when Store.Invoices.FirstOrDefault(i => i.Id == o.Id) is { } inv: Nav.Open(new InvoiceWindow(inv)); break;
            case { Kind: "quotation" } when Store.QuotationById(o.Id) is { } q: Nav.Open(new QuotationWindow(q)); break;
            case { Kind: "item" } when Store.ItemById(o.Id) is { } it: Nav.Open(new ItemWindow(it)); break;
            case { Kind: "purchase" } when Store.Purchases.FirstOrDefault(p => p.Id == o.Id) is { } p: S.ShowCost = true; Nav.Open(new PurchaseWindow(p)); break;
        }
    }

    void ExportCsv()
    {
        if (Table() is not { } t) return;
        var rows = t.FilteredSorted(search, sortColumn, ascending);
        var name = $"{t.Title.Split(" (")[0]} {PeriodLabel}.csv";
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = string.Join("-", name.Split(System.IO.Path.GetInvalidFileNameChars())), Filter = "CSV for Excel|*.csv", DefaultExt = ".csv" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, t.Csv(rows, S.ShowCost), new System.Text.UTF8Encoding(false));
    }

    void PrintReport()
    {
        if (Table() is not { } t) return;
        var rows = t.FilteredSorted(search, sortColumn, ascending);
        var pdf = ReportPdf.Render(t, rows, PeriodLabel, Store.Settings, S.ShowCost);
        var file = string.Join("-", $"{t.Title} {PeriodLabel}.pdf".Split(System.IO.Path.GetInvalidFileNameChars()));
        new PdfPreviewWindow(pdf, t.Title, file).ShowDialog();
    }

    // MARK: Overview

    UIElement Overview()
    {
        var o = Store.GetOverview(Range);
        var cur = Store.Settings.Currency;
        var tiles = new WrapPanel();
        void Tile(string title, string value, string sub, string icon, bool sensitive = false, bool negative = false)
        {
            var hidden = sensitive && !S.ShowCost;
            var c = Ui.Card(Ui.Row(Ui.IconTile(icon, sensitive ? Ui.Orange : Ui.Blue, 48, 30), Ui.Col(1,
                Ui.Text(title, 12, FontWeights.Medium, Ui.Muted), Ui.Text(hidden ? "••••••" : value, 20, FontWeights.Bold, hidden ? Ui.Muted : negative ? Ui.Red : Ui.Navy),
                Ui.Text(hidden ? "hidden" : sub, 11.5, null, Ui.Muted)).Also(p => p.Margin = new Thickness(14, 0, 0, 0))), 16);
            c.Width = 262; c.Margin = new Thickness(0, 0, 14, 14);
            tiles.Children.Add(c);
        }
        Tile("Net Sales", Fmt.Money(o.NetSales, cur), "before sales tax", "invoices");
        Tile("Sales Tax Collected", Fmt.Money(o.Tax, cur), $"{o.Bills} bills", "quotations");
        Tile("Total Billed", Fmt.Money(o.Total, cur), $"avg {Fmt.Money(o.AvgBill, cur)} per bill", "money");
        Tile("Cost of Goods Sold", Fmt.Money(o.Cost, cur), $"{Fmt.Qty(o.Qty)} units sold", "inventory", true);
        Tile("Gross Profit", Fmt.Money(o.Profit, cur), o.Margin is { } m ? $"margin {Fmt.Qty(Fmt.R2(m))}%" : "—", "reports", true, o.Profit < 0);
        Tile("Purchases (Stock In)", Fmt.Money(o.Purchases, cur), $"{o.PurchaseBills} purchase invoices", "purchases", true);
        Tile("Open Quotations", Fmt.Money(o.OpenQuotesValue, cur), $"{o.OpenQuotes} not invoiced yet", "pending");
        Tile("Stock Value", Fmt.Money(o.StockCost, cur), $"at cost · {Fmt.Money(o.StockSale, cur)} at sale", "stockin", true);

        var chart = Ui.Card(new SalesChart(o.Points, o.ByMonth, S.ShowCost, cur) { Height = 300 });
        var rankings = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        rankings.ColumnDefinitions.Add(new ColumnDefinition());
        rankings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        rankings.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Ranking("Top Items by Sales", o.TopItems, cur);
        var b = Ranking("Top Customers by Sales", o.TopCustomers, cur);
        Grid.SetColumn(b, 2);
        rankings.Children.Add(a); rankings.Children.Add(b);
        return Ui.Scroll(Ui.Col(0, tiles, chart, rankings));
    }

    UIElement Ranking(string title, List<(string Name, double Sales, double Profit)> data, string cur)
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Heading(title));
        if (data.Count == 0) p.Children.Add(Ui.Caption("No sales in this period."));
        var top = data.Count > 0 ? data.Max(d => d.Sales) : 1;
        foreach (var d in data)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
            var right = Ui.Row(Ui.Text(Fmt.Money(d.Sales, cur), 13, FontWeights.Medium));
            if (S.ShowCost) right.Children.Add(Ui.Text($"   profit {Fmt.Money(d.Profit)}", 12, null, d.Profit < 0 ? Ui.Red : Ui.Muted));
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(Ui.Text(d.Name, 13));
            p.Children.Add(row);
            var bar = new Grid { Height = 6 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, d.Sales / Math.Max(top, 1)), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - d.Sales / Math.Max(top, 1)), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = SalesChart.SalesBrush, CornerRadius = new CornerRadius(3) });
            p.Children.Add(bar);
        }
        return Ui.Card(p);
    }
}

/// <summary>Net sales (and profit) bars by day or month, one axis, hover for exact figures.</summary>
sealed class SalesChart : Border
{
    // Validated categorical slots 1 and 2 (same as the Mac app).
    public static readonly Brush SalesBrush = Ui.Hex("#2A78D6");
    public static readonly Brush ProfitBrush = Ui.Hex("#EB6834");
    readonly List<ChartPoint> points;
    readonly bool byMonth, showProfit;
    readonly string cur;
    readonly Canvas canvas = new() { ClipToBounds = true };

    public SalesChart(List<ChartPoint> points, bool byMonth, bool showProfit, string cur)
    {
        this.points = points; this.byMonth = byMonth; this.showProfit = showProfit; this.cur = cur;
        var title = Ui.Heading(showProfit ? $"Net sales and profit, {(byMonth ? "by month" : "by day")}" : $"Net sales, {(byMonth ? "by month" : "by day")}");
        var legend = Ui.Row();
        if (showProfit)
        {
            UIElement Key(Brush b, string t) => Ui.Row(new Rectangle { Width = 10, Height = 10, Fill = b, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0) }, Ui.Text(t + "    ", 12, null, Ui.Muted));
            legend.Children.Add(Key(SalesBrush, "Net Sales"));
            legend.Children.Add(Key(ProfitBrush, "Profit"));
        }
        var head = new DockPanel();
        DockPanel.SetDock(legend, Dock.Right);
        head.Children.Add(legend);
        head.Children.Add(title);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(canvas.Also(c => c.Margin = new Thickness(0, 10, 0, 0)));
        Child = dock;
        canvas.SizeChanged += (_, _) => Draw();
    }

    static string Compact(double v) => Math.Abs(v) >= 1_000_000 ? $"{v / 1_000_000:0.0}M" : Math.Abs(v) >= 1_000 ? $"{v / 1_000:0}K" : $"{v:0}";

    void Draw()
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 50 || h < 50) return;
        if (points.Count == 0) { canvas.Children.Add(Ui.Text("No sales in this period.", 13, null, Ui.Muted).Also(t => { Canvas.SetLeft(t, w / 2 - 70); Canvas.SetTop(t, h / 2 - 10); })); return; }
        const double left = 8, right = 54, bottom = 22;
        double plotW = w - left - right, plotH = h - bottom - 6;
        var max = Math.Max(1, points.Max(p => Math.Max(p.Sales, showProfit ? p.Profit : 0)));
        var min = showProfit ? Math.Min(0, points.Min(p => p.Profit)) : 0;
        double Y(double v) => 6 + plotH * (1 - (v - min) / (max - min));
        for (int i = 0; i <= 4; i++)
        {
            var v = min + (max - min) * i / 4;
            canvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = Y(v), Y2 = Y(v), Stroke = Ui.Line, StrokeThickness = 1 });
            var lbl = Ui.Text(Compact(v), 11, null, Ui.Muted);
            Canvas.SetLeft(lbl, left + plotW + 6); Canvas.SetTop(lbl, Y(v) - 8);
            canvas.Children.Add(lbl);
        }
        double slot = plotW / points.Count;
        int series = showProfit ? 2 : 1;
        double barW = Math.Max(2, Math.Min(28, (slot - 6) / series - 2));
        int labelEvery = Math.Max(1, (int)Math.Ceiling(points.Count / (plotW / 70)));
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double x0 = left + slot * i + (slot - (barW * series + 2 * (series - 1))) / 2;
            void Bar(double value, Brush brush, double x)
            {
                double y1 = Y(Math.Max(0, value)), y2 = Y(Math.Min(0, value));
                var r = new Rectangle { Width = barW, Height = Math.Max(1, y2 - y1), Fill = brush, RadiusX = 3, RadiusY = 3 };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y1);
                canvas.Children.Add(r);
            }
            Bar(p.Sales, SalesBrush, x0);
            if (showProfit) Bar(p.Profit, ProfitBrush, x0 + barW + 2);
            var hit = new Rectangle { Width = slot, Height = plotH, Fill = Brushes.Transparent };
            var label = byMonth ? p.Date.ToString("MMMM yyyy") : Fmt.Date(p.Date);
            hit.ToolTip = $"{label}\nNet sales: {Fmt.Money(p.Sales, cur)}" + (showProfit ? $"\nProfit: {Fmt.Money(p.Profit, cur)}" : "");
            ToolTipService.SetInitialShowDelay(hit, 0);
            Canvas.SetLeft(hit, left + slot * i); Canvas.SetTop(hit, 6);
            canvas.Children.Add(hit);
            if (i % labelEvery == 0)
            {
                var xl = Ui.Text(byMonth ? p.Date.ToString("MMM yy") : p.Date.ToString("d MMM"), 11, null, Ui.Muted);
                Canvas.SetLeft(xl, left + slot * i + slot / 2 - 18); Canvas.SetTop(xl, h - bottom + 4);
                canvas.Children.Add(xl);
            }
        }
    }
}

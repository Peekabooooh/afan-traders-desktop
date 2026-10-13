using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>Line items table for quotations, delivery challans and invoices.</summary>
sealed class LinesEditor : Border
{
    public enum Mode { Quotation, Challan, Invoice }

    public List<DocLine> Lines { get; }
    public event Action? Changed;
    readonly Mode mode;
    readonly bool locked;
    static AppServices S => AppServices.Current;

    bool ShowPrices => mode != Mode.Challan;
    bool ShowCost => ShowPrices && S.ShowCost;
    bool EditQty => mode == Mode.Quotation && !locked;
    bool EditRate => mode != Mode.Challan && !locked;
    bool CanDelete => mode == Mode.Quotation && !locked;

    public LinesEditor(IEnumerable<DocLine> lines, Mode mode, bool locked = false)
    {
        Lines = lines.ToList();
        this.mode = mode;
        this.locked = locked;
        Background = Ui.CardBg; BorderBrush = Ui.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(10);
        Rebuild();
    }

    Grid Columns()
    {
        var g = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        void C(double w) => g.ColumnDefinitions.Add(new ColumnDefinition { Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        C(30); C(130); C(0); C(60); C(90);
        if (ShowPrices) { C(120); C(120); }
        if (ShowCost) { C(95); C(105); }
        if (CanDelete) C(34);
        return g;
    }

    static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    public void Rebuild()
    {
        var root = new StackPanel();
        var head = Columns();
        head.Margin = new Thickness(12, 10, 12, 8);
        string[] titles = { "#", "Item ID", "Item / Description", "Unit", "Qty" };
        int c = 0;
        foreach (var t in titles) Put(head, Ui.Text(t, 12, FontWeights.SemiBold, Ui.Muted).Also(x => { if (t == "Qty") x.HorizontalAlignment = HorizontalAlignment.Right; }), c++);
        if (ShowPrices) { Put(head, Right(Ui.Text(mode == Mode.Invoice ? "Invoice Rate" : "Rate", 12, FontWeights.SemiBold, Ui.Muted)), c++); Put(head, Right(Ui.Text("Amount", 12, FontWeights.SemiBold, Ui.Muted)), c++); }
        if (ShowCost) { Put(head, Right(Ui.Text("Cost", 12, FontWeights.SemiBold, Ui.Muted)), c++); Put(head, Right(Ui.Text("Profit", 12, FontWeights.SemiBold, Ui.Muted)), c++); }
        root.Children.Add(head);
        root.Children.Add(new Border { Height = 1, Background = Ui.Line });
        var rows = new StackPanel();
        if (Lines.Count == 0)
            rows.Children.Add(Ui.Text(mode == Mode.Quotation ? "No items yet. Search above to add items from inventory." : "No items.", 13, null, Ui.Muted)
                .Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 26, 0, 26); }));
        for (int i = 0; i < Lines.Count; i++) rows.Children.Add(RowFor(i));
        root.Children.Add(rows);
        Child = root;
    }

    static FrameworkElement Right(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Right; return e; }

    UIElement RowFor(int index)
    {
        var line = Lines[index];
        var item = S.Store.ItemById(line.ItemId);
        var g = Columns();
        g.Margin = new Thickness(12, 6, 12, 6);
        int c = 0;
        Put(g, Ui.Text($"{index + 1}", 13, null, Ui.Muted), c++);
        Put(g, Ui.Row(Ui.Thumb(item?.Image, 24).Also(t => t.Margin = new Thickness(0, 0, 6, 0)), Ui.Text(line.Code, 13, FontWeights.SemiBold)), c++);

        var desc = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        if (mode == Mode.Quotation && !locked)
        {
            desc.Children.Add(Ui.Input(line.Name, v => Update(index, l => l with { Name = v })));
            desc.Children.Add(Ui.Input(line.Details, v => Update(index, l => l with { Details = v })).Also(t => { t.FontSize = 12; t.MinHeight = 26; t.Margin = new Thickness(0, 3, 0, 0); t.ToolTip = "Description (optional)"; }));
        }
        else
        {
            desc.Children.Add(Ui.Text(line.Name, 13));
            if (line.Details.Length > 0) desc.Children.Add(Ui.Text(line.Details, 12, null, Ui.Muted));
        }
        var stockWarn = Ui.Text("", 11.5, null, Ui.Orange);
        if (mode == Mode.Quotation) desc.Children.Add(stockWarn);
        Put(g, desc, c++);
        Put(g, Ui.Text(line.Unit, 13, null, Ui.Muted), c++);

        var amount = Ui.Text(Fmt.Money(line.Total), 13, FontWeights.SemiBold);
        var cost = Ui.Text("", 13, null, Ui.Muted);
        var profit = Ui.Text("", 13);
        var quoted = Ui.Text("", 11, null, Ui.Muted);
        void Refresh()
        {
            var l = Lines[index];
            amount.Text = Fmt.Money(l.Total);
            if (item != null)
            {
                var p = Fmt.R2((l.Rate - item.CostPrice) * l.Qty);
                cost.Text = Fmt.Money(item.CostPrice);
                profit.Text = Fmt.Money(p);
                profit.Foreground = p < 0 ? Ui.Red : Ui.Green;
            }
            else { cost.Text = "—"; profit.Text = "—"; }
            stockWarn.Text = item != null && item.Stock < l.Qty ? $"⚠ Only {Fmt.Qty(item.Stock)} in stock" : "";
            stockWarn.Visibility = stockWarn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            quoted.Text = mode == Mode.Invoice && l.BaseRate != l.Rate ? $"Quoted: {Fmt.Money(l.BaseRate)}" : "";
        }

        Put(g, Right(EditQty ? Ui.Number(line.Qty, v => { Update(index, l => l with { Qty = v }); Refresh(); }, 80) : Ui.Text(Fmt.Qty(line.Qty), 13)), c++);
        if (ShowPrices)
        {
            var rate = Ui.Col(0, Right(EditRate ? Ui.Number(line.Rate, v => { Update(index, l => l with { Rate = v }); Refresh(); }, 110) : Ui.Text(Fmt.Money(line.Rate), 13)), Right(quoted));
            Put(g, Right(rate), c++);
            Put(g, Right(amount), c++);
        }
        if (ShowCost) { Put(g, Right(cost), c++); Put(g, Right(profit), c++); }
        if (CanDelete)
        {
            var del = new Button { Content = "🗑", Foreground = Ui.Red, Padding = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Remove this item" };
            del.Click += (_, _) => { Lines.RemoveAt(index); Rebuild(); Changed?.Invoke(); };
            Put(g, Right(del), c++);
        }
        Refresh();
        return new Border { Child = g, Background = index % 2 == 1 ? Ui.Zebra : Brushes.Transparent, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0) };
    }

    void Update(int index, Func<DocLine, DocLine> f)
    {
        if (index >= Lines.Count) return;
        Lines[index] = f(Lines[index]);
        Changed?.Invoke();
    }

    public void Add(Item it)
    {
        var existing = Lines.FindIndex(l => l.ItemId == it.Id);
        if (existing >= 0) Lines[existing] = Lines[existing] with { Qty = Lines[existing].Qty + 1 };
        else Lines.Add(new DocLine { ItemId = it.Id, Code = it.Code, Name = it.Name, Details = it.Details, Unit = it.Unit, Qty = 1, Rate = it.SalePrice, BaseRate = it.SalePrice });
        Rebuild();
        Changed?.Invoke();
    }

    public void SetRates(Func<DocLine, double> rate)
    {
        for (int i = 0; i < Lines.Count; i++) Lines[i] = Lines[i] with { Rate = Fmt.R2(rate(Lines[i])) };
        Rebuild();
        Changed?.Invoke();
    }
}

/// <summary>Subtotal / sales tax / grand total (and profit while costs are shown).</summary>
sealed class TotalsBox : Border
{
    readonly TextBlock subtotal = Ui.Text("", 14), tax = Ui.Text("", 14), grand = Ui.Text("", 19, FontWeights.Bold, Ui.Navy), profit = Ui.Text("", 12.5, FontWeights.SemiBold);
    readonly DockPanel profitRow;
    readonly Func<(double Sub, double Rate, double Profit)> source;
    static string Cur => AppServices.Current.Store.Settings.Currency;

    public TotalsBox(Func<(double Sub, double Rate, double Profit)> source, double taxRate, Action<double>? taxChanged)
    {
        this.source = source;
        Width = 360; Background = Ui.CardBg; BorderBrush = Ui.Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(10); Padding = new Thickness(16);
        DockPanel Line(UIElement left, UIElement right) { var d = new DockPanel { Margin = new Thickness(0, 4, 0, 4) }; DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); d.Children.Add(left); return d; }
        UIElement taxLabel = taxChanged == null ? Ui.Text($"Sales Tax ({Fmt.Qty(taxRate)}%)", 14)
            : Ui.Row(Ui.Text("Sales Tax  ", 14), Ui.Number(taxRate, v => { taxChanged(v); Update(); }, 60), Ui.Text(" %", 14));
        profitRow = Line(Ui.Text("Profit (before tax)", 12.5, null, Ui.Muted), profit);
        Child = Ui.Col(0, Line(Ui.Text("Subtotal", 14), subtotal), Line(taxLabel, tax),
            new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 6, 0, 6) },
            Line(Ui.Text("Grand Total", 15, FontWeights.SemiBold), grand), profitRow);
        Update();
    }

    public void Update()
    {
        var (sub, rate, p) = source();
        var t = Fmt.R2(sub * rate / 100);
        subtotal.Text = Fmt.Money(sub, Cur);
        tax.Text = Fmt.Money(t, Cur);
        grand.Text = Fmt.Money(sub + t, Cur);
        profit.Text = Fmt.Money(p, Cur);
        profit.Foreground = p < 0 ? Ui.Red : Ui.Green;
        profitRow.Visibility = AppServices.Current.ShowCost ? Visibility.Visible : Visibility.Collapsed;
    }
}

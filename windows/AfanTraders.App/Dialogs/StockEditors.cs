using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

sealed class ItemWindow : EditorWindow
{
    Item item;
    readonly Border picture = new() { Width = 150, Height = 150, CornerRadius = new CornerRadius(24), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };

    public ItemWindow(Item it) : base(900, it.Id == 0 ? 520 : 720)
    {
        item = it;
        Title = item.Id == 0 ? "New Item" : "Edit Item";
        var cur = Store.Settings.Currency;
        UpdatePicture();
        var picCol = Ui.Col(8, picture,
            Ui.Row(Ui.Btn(item.Image == null ? "Add Picture…" : "Change…", () => { if (Images.Pick() is { } d) { item = item with { Image = d }; UpdatePicture(); } }),
                   Ui.Btn("Remove", () => { item = item with { Image = null }; UpdatePicture(); })),
            Ui.Caption("Picture is optional"));
        picCol.Width = 190;

        UIElement costField = S.ShowCost
            ? Ui.Number(item.CostPrice, v => item = item with { CostPrice = v }, 130)
            : Ui.Btn("Hidden – click to show", () => { S.ShowCost = true; OpenNext(() => Nav.Open(new ItemWindow(item))); });
        var form = Ui.Col(12,
            Ui.Row(Ui.Field("Item ID *", Ui.Input(item.Code, v => item = item with { Code = v }, width: 190)), new Border { Width = 12 },
                   Ui.Field("Item name *", Ui.Input(item.Name, v => item = item with { Name = v }, width: 420))),
            Ui.Field("Description", Ui.Input(item.Details, v => item = item with { Details = v }, multiline: true)),
            Ui.Row(Ui.Field("Unit", Ui.Input(item.Unit, v => item = item with { Unit = v }, width: 90)), new Border { Width = 12 },
                   Ui.Field($"Sale price ({cur})", Ui.Number(item.SalePrice, v => item = item with { SalePrice = v }, 130)), new Border { Width = 12 },
                   Ui.Field($"Cost price ({cur})", costField), new Border { Width = 12 },
                   Ui.Field("In stock", Ui.Number(item.Stock, v => item = item with { Stock = v }, 110))),
            Ui.Caption("Tip: stock is normally added through purchase invoices. Change “In stock” here only to correct a count."));

        var top = new DockPanel();
        DockPanel.SetDock(picCol, Dock.Left);
        top.Children.Add(picCol);
        top.Children.Add(form.Also(f => f.Margin = new Thickness(16, 0, 0, 0)));
        var body = new StackPanel();
        body.Children.Add(top);
        if (item.Id != 0) body.Children.Add(new Border { Child = History(), Margin = new Thickness(0, 18, 0, 0) });

        var left = new List<UIElement>();
        if (item.Id != 0) left.Add(Ui.Btn("Delete", Delete, tint: Ui.Red));
        Layout(Ui.Header(Title, item.Id == 0 ? "Add a product to your inventory" : item.Code, "inventory"), body,
               Ui.Footer(left, new UIElement[] { Ui.Btn("Cancel", Close), Ui.Btn("Save Item", () => { Store.SaveItem(item); Close(); }, primary: true) }));
    }

    protected override void OnSaveShortcut() => Ui.Safe(() => { Store.SaveItem(item); Close(); });

    void UpdatePicture() => picture.Child = Ui.Thumb(item.Image, 148);

    UIElement History()
    {
        var moves = Store.StockHistory(item);
        var list = new StackPanel();
        list.Children.Add(Ui.Heading("Stock History"));
        if (moves.Count == 0) list.Children.Add(Ui.Caption("No purchase invoices or deliveries for this item yet."));
        foreach (var m in moves.Take(30))
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var w in new[] { 110.0, 260, 0, 110, 80 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            void Put(UIElement e, int c) { Grid.SetColumn(e, c); g.Children.Add(e); }
            Put(Ui.Text(Fmt.Date(m.Date), 13, null, Ui.Muted), 0);
            Put(Ui.Text(m.Reference, 13, FontWeights.SemiBold), 1);
            Put(Ui.Text(m.Party.Length == 0 ? "—" : m.Party, 13, null, Ui.Muted), 2);
            if (m.Cost is { } c) Put(Ui.Text(S.ShowCost ? "@ " + Fmt.Money(c) : "••••", 13, null, Ui.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right), 3);
            Put(Ui.Text(m.Qty > 0 ? "+" + Fmt.Qty(m.Qty) : Fmt.Qty(m.Qty), 13, FontWeights.SemiBold, m.Qty > 0 ? Ui.Green : Ui.Red).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right), 4);
            list.Children.Add(g);
        }
        return Ui.Card(list);
    }

    void Delete()
    {
        if (!Ui.Confirm($"Delete {item.Name}?\nOld quotations and invoices keep their copy of this item.")) return;
        Store.DeleteItem(item);
        Close();
    }
}

/// <summary>Bulk item adder: one purchase invoice number, many rows. Existing Item IDs add to stock.</summary>
sealed class PurchaseWindow : EditorWindow
{
    Purchase p;
    readonly List<PurchaseLine> lines;
    readonly StackPanel rows = new();
    readonly TextBlock summary = Ui.Text("", 14, FontWeights.SemiBold);

    public PurchaseWindow(Purchase purchase) : base(1280, 820)
    {
        p = purchase;
        lines = p.Lines.ToList();
        if (p.Id == 0) while (lines.Count < 5) lines.Add(new PurchaseLine { Qty = 0 });
        Title = p.Id == 0 ? "Add Items / Stock In" : $"Purchase Invoice {p.Number}";

        var head = Ui.Card(Ui.Row(
            Ui.Field("Purchase invoice No. *", Ui.Input(p.Number, v => p = p with { Number = v }, width: 260)), new Border { Width = 12 },
            Ui.Field("Supplier", Ui.Input(p.Supplier, v => p = p with { Supplier = v }, width: 420)), new Border { Width = 12 },
            Ui.Field("Date", Ui.Date(p.Date, v => p = p with { Date = v }))));
        var table = new Border { Child = rows, Background = Ui.CardBg, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(table.Also(t => t.Margin = new Thickness(0, 14, 0, 10)));
        body.Children.Add(Ui.Row(Ui.Btn("+ Add Row", () => AddRows(1)), Ui.Btn("+ Add 5 Rows", () => AddRows(5)),
                                 Ui.Caption("    Empty rows are ignored. Type an existing Item ID to use its saved details. Picture is optional.")));
        body.Children.Add(Forms.Bottom(Ui.Field("Notes", Ui.Input(p.Notes, v => p = p with { Notes = v }, width: 420)), Ui.Card(summary, 14)).Also(d => d.Margin = new Thickness(0, 14, 0, 0)));
        Rebuild();

        var left = new List<UIElement>();
        if (p.Id != 0) left.Add(Ui.Btn("Delete", Delete, tint: Ui.Red));
        Layout(Ui.Header(Title, "Enter all items from one purchase invoice. Existing Item IDs add to their stock.", "stockin"), body,
               Ui.Footer(left, new UIElement[] { Ui.Btn("Cancel", Close), Ui.Btn("Save & Add to Stock", SaveAndClose, primary: true, icon: "stockin") }));
    }

    protected override void OnSaveShortcut() => Ui.Safe(SaveAndClose);

    void SaveAndClose()
    {
        Store.SavePurchase(p with { Lines = lines.ToList() });
        Close();
    }

    static readonly double[] Widths = { 28, 170, 0, 0, 70, 80, 100, 100, 100, 40, 30 };

    Grid Columns()
    {
        var g = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        foreach (var w in Widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    static void Put(Grid g, UIElement e, int c) { Grid.SetColumn(e, c); if (e is FrameworkElement f && c > 0) f.Margin = new Thickness(4, 0, 4, 0); g.Children.Add(e); }

    void Rebuild()
    {
        rows.Children.Clear();
        var head = Columns();
        head.Margin = new Thickness(12, 10, 12, 8);
        string[] titles = { "#", "Item ID *", "Name *", "Description", "Unit", "Qty *", "Cost price", "Sale price", "Total", "Pic", "" };
        for (int i = 0; i < titles.Length; i++)
            Put(head, Ui.Text(titles[i], 12, FontWeights.SemiBold, Ui.Muted).Also(t => { if (i is >= 5 and <= 8) t.HorizontalAlignment = HorizontalAlignment.Right; }), i);
        rows.Children.Add(head);
        rows.Children.Add(new Border { Height = 1, Background = Ui.Line });
        for (int i = 0; i < lines.Count; i++) rows.Children.Add(RowFor(i));
        UpdateSummary();
    }

    Item? Existing(PurchaseLine l) => Store.ItemById(l.ItemId) ?? Store.ItemByCode(l.Code);

    UIElement RowFor(int i)
    {
        var l = lines[i];
        var g = Columns();
        g.Margin = new Thickness(12, 6, 12, 6);
        var status = Ui.Text("", 11, null, Ui.Blue);
        var total = Ui.Text(Fmt.Money(l.Total), 13, FontWeights.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right);
        var name = Ui.Input(l.Name, v => Set(i, x => x with { Name = v }));
        var details = Ui.Input(l.Details, v => Set(i, x => x with { Details = v }));
        var unit = Ui.Input(l.Unit, v => Set(i, x => x with { Unit = v }));
        var qty = Ui.Number(l.Qty, v => { Set(i, x => x with { Qty = v }); RefreshRow(); }, 76);
        var cost = Ui.Number(l.Cost, v => { Set(i, x => x with { Cost = v }); RefreshRow(); }, 96);
        var sale = Ui.Number(l.SalePrice, v => Set(i, x => x with { SalePrice = v }), 96);

        void RefreshRow()
        {
            var cur = lines[i];
            var ex = Existing(cur);
            total.Text = Fmt.Money(cur.Total);
            name.IsEnabled = details.IsEnabled = unit.IsEnabled = ex == null;
            status.Text = ex != null ? $"In stock {Fmt.Qty(ex.Stock)} → {Fmt.Qty(ex.Stock + cur.Qty)}" : cur.Code.T().Length > 0 ? "NEW ITEM" : "";
            status.Foreground = ex != null ? Ui.Blue : Ui.Green;
            UpdateSummary();
        }

        var code = new TextBox { Text = l.Code, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        var suggestions = new System.Windows.Controls.Primitives.Popup { StaysOpen = false, PlacementTarget = code, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, AllowsTransparency = true };
        var list = new ListBox { Width = 340, MaxHeight = 260 };
        suggestions.Child = new Border { Child = list, Background = Brushes.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        void Fill(Item it)
        {
            lines[i] = lines[i] with { ItemId = it.Id, Code = it.Code, Name = it.Name, Details = it.Details, Unit = it.Unit, Cost = it.CostPrice, SalePrice = it.SalePrice, Qty = lines[i].Qty <= 0 ? 1 : lines[i].Qty };
            suggestions.IsOpen = false;
            if (i == lines.Count - 1) lines.Add(new PurchaseLine { Qty = 0 });
            Rebuild();
        }
        code.TextChanged += (_, _) =>
        {
            var v = code.Text;
            var exact = Store.ItemByCode(v);
            lines[i] = lines[i] with { Code = v, ItemId = exact?.Id, Qty = v.T().Length > 0 && lines[i].Qty <= 0 ? 1 : lines[i].Qty };
            if (exact != null && l.ItemId != exact.Id) { Fill(exact); return; }
            var q = v.T().ToLowerInvariant();
            list.Items.Clear();
            if (q.Length > 0)
                foreach (var it in Store.Items.Where(x => x.Code.ToLowerInvariant().Contains(q) || x.Name.ToLowerInvariant().Contains(q)).Take(6))
                    list.Items.Add(new ListBoxItem { Content = Ui.Row(Ui.Thumb(it.Image, 24), Ui.Text($"  {it.Code}  ", 13, FontWeights.SemiBold), Ui.Text(it.Name, 12.5, null, Ui.Muted)), Tag = it, Padding = new Thickness(6, 4, 6, 4) });
            suggestions.IsOpen = list.Items.Count > 0 && code.IsKeyboardFocused;
            if (qty.Text == "0" && v.T().Length > 0) qty.Text = "1";
            RefreshRow();
        };
        list.PreviewMouseLeftButtonUp += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: Item it }) Fill(it); };
        code.PreviewKeyDown += (_, e) =>
        {
            if (!suggestions.IsOpen) return;
            if (e.Key == System.Windows.Input.Key.Down) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
            if (e.Key == System.Windows.Input.Key.Enter && list.SelectedItem is ListBoxItem { Tag: Item it }) { Fill(it); e.Handled = true; }
        };

        var pic = new Button { Padding = new Thickness(2), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Add a picture (optional)" };
        void UpdatePic() => pic.Content = lines[i].Image != null || Existing(lines[i])?.Image != null ? Ui.Thumb(lines[i].Image ?? Existing(lines[i])?.Image, 26) : Ui.Text("🖼", 16, null, Ui.Muted);
        pic.Click += (_, _) => { if (Images.Pick() is { } d) { lines[i] = lines[i] with { Image = d }; UpdatePic(); } };
        UpdatePic();
        var remove = new Button { Content = "✕", Foreground = Ui.Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Remove row" };
        remove.Click += (_, _) => { lines.RemoveAt(i); if (lines.Count == 0) lines.Add(new PurchaseLine { Qty = 0 }); Rebuild(); };

        Put(g, Ui.Text($"{i + 1}", 13, null, Ui.Muted), 0);
        Put(g, Ui.Col(2, code, status), 1);
        Put(g, name, 2); Put(g, details, 3); Put(g, unit, 4);
        Put(g, qty.Also(x => x.HorizontalAlignment = HorizontalAlignment.Right), 5);
        Put(g, cost.Also(x => x.HorizontalAlignment = HorizontalAlignment.Right), 6);
        Put(g, sale.Also(x => x.HorizontalAlignment = HorizontalAlignment.Right), 7);
        Put(g, total, 8); Put(g, pic, 9); Put(g, remove, 10);
        RefreshRow();
        return new Border { Child = g, Background = i % 2 == 1 ? Ui.Zebra : Brushes.Transparent };
    }

    void Set(int i, Func<PurchaseLine, PurchaseLine> f) { if (i < lines.Count) lines[i] = f(lines[i]); }

    void AddRows(int n) { for (int k = 0; k < n; k++) lines.Add(new PurchaseLine { Qty = 0 }); Rebuild(); }

    void UpdateSummary()
    {
        var filled = lines.Where(l => !l.IsBlank).ToList();
        var news = filled.Count(l => Existing(l) == null);
        summary.Text = $"{filled.Count} items   ·   {news} new   ·   total qty {Fmt.Qty(filled.Sum(l => l.Qty))}   ·   {Fmt.Money(filled.Sum(l => l.Total), Store.Settings.Currency)} (cost)";
    }

    void Delete()
    {
        if (!Ui.Confirm("Delete this purchase invoice?\nThe quantities will be removed from stock again.")) return;
        Store.DeletePurchase(p);
        Close();
    }
}

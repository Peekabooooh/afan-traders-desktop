using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>DataGrid column whose cell is built in code (no templates, no binding strings).</summary>
sealed class CodeColumn<T> : DataGridColumn
{
    readonly Func<T, FrameworkElement> build;
    public CodeColumn(string header, double width, Func<T, FrameworkElement> build, string? sortProperty = null)
    {
        Header = header;
        Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width);
        MinWidth = width <= 0 ? 170 : 40;   // the flexible column (customer, name…) never gets squeezed away
        this.build = build;
        SortMemberPath = sortProperty;
        CanUserSort = sortProperty != null;
        IsReadOnly = true;
    }
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
        dataItem is T t ? build(t).Also(e => e.VerticalAlignment = VerticalAlignment.Center) : new TextBlock();
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);
}

static class Grids
{
    public static DataGrid Make()
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Ui.Line, RowHeight = 38, AlternatingRowBackground = Ui.Zebra, Background = Ui.CardBg,
            BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CanUserResizeRows = false, CanUserReorderColumns = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            EnableRowVirtualization = true,
        };
        return g;
    }

    public static void Add<T>(this DataGrid g, string header, double width, Func<T, string> text, string? sort = null, bool right = false, bool bold = false, Func<T, Brush?>? color = null)
    {
        g.Columns.Add(new CodeColumn<T>(header, width, item =>
        {
            var t = Ui.Text(text(item), 13, bold ? FontWeights.SemiBold : FontWeights.Normal, color?.Invoke(item));
            t.Margin = new Thickness(6, 0, 6, 0);
            if (right) t.TextAlignment = TextAlignment.Right;
            t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            return t;
        }, sort));
    }

    public static void AddCell<T>(this DataGrid g, string header, double width, Func<T, FrameworkElement> build) =>
        g.Columns.Add(new CodeColumn<T>(header, width, item => build(item).Also(e => e.Margin = new Thickness(6, 0, 6, 0))));

    public static void OnOpen<T>(this DataGrid g, Action<T> open) where T : class =>
        g.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(g, d) is DataGridRow row && row.Item is T t) Ui.Safe(() => open(t));
        };

    public static void Menu<T>(this DataGrid g, Func<T, IEnumerable<(string Label, Action Act)>> items) where T : class
    {
        g.ContextMenuOpening += (_, e) =>
        {
            if (g.SelectedItem is not T t) { e.Handled = true; return; }
            var menu = new ContextMenu();
            foreach (var (label, act) in items(t))
            {
                if (label == "-") { menu.Items.Add(new Separator()); continue; }
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) => Ui.Safe(act);
                menu.Items.Add(mi);
            }
            g.ContextMenu = menu;
        };
        g.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(g, d) is DataGridRow row) row.IsSelected = true;
        };
        g.ContextMenu = new ContextMenu();
    }
}

static class PageParts
{
    /// <summary>Title row with optional search box and buttons on the right.</summary>
    public static Border TitleBar(string title, TextBox? search, params UIElement[] actions)
    {
        var dock = new DockPanel { LastChildFill = false };
        var t = Ui.Text(title, 22, FontWeights.SemiBold);
        DockPanel.SetDock(t, Dock.Left);
        dock.Children.Add(t);
        foreach (var a in actions.Reverse()) { DockPanel.SetDock(a, Dock.Right); dock.Children.Add(a); }
        if (search != null)
        {
            var box = Ui.Row(Ui.Text("🔍", 13, null, Ui.Muted).Also(x => x.Margin = new Thickness(0, 0, 6, 0)), search);
            box.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(box, Dock.Right);
            dock.Children.Add(box);
        }
        return new Border { Child = dock, Padding = new Thickness(20, 16, 20, 12) };
    }

    public static TextBox Search(string hint, Action<string> changed)
    {
        var t = new TextBox { Width = 300, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = hint };
        t.TextChanged += (_, _) => changed(t.Text);
        return t;
    }

    public static DockPanel Layout(UIElement top, UIElement main, UIElement? bottom = null)
    {
        var d = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        d.Children.Add(top);
        if (bottom != null) { DockPanel.SetDock(bottom, Dock.Bottom); d.Children.Add(bottom); }
        d.Children.Add(new Border { Child = main, Margin = new Thickness(20, 0, 20, bottom == null ? 16 : 0) });
        return d;
    }

    public static Border Empty(string icon, string title, string text, params UIElement[] actions)
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Ui.Icon(icon, 64).Also(i => i.Margin = new Thickness(0, 0, 0, 12)));
        p.Children.Add(Ui.Text(title, 18, FontWeights.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        p.Children.Add(Ui.Text(text, 13, null, Ui.Muted, TextWrapping.Wrap).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.MaxWidth = 420; t.Margin = new Thickness(0, 6, 0, 12); }));
        if (actions.Length > 0) p.Children.Add(Ui.Row(actions).Also(r => r.HorizontalAlignment = HorizontalAlignment.Center));
        return new Border { Child = p, Background = Ui.CardBg, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), MinHeight = 320 };
    }

    public static Border StagePill(OrderStage s) => s switch
    {
        OrderStage.NeedsChallan => Ui.Pill("Needs DC", Ui.Blue),
        OrderStage.AwaitingApproval => Ui.Pill("DC Pending", Ui.Orange),
        OrderStage.ReadyToInvoice => Ui.Pill("Ready to Invoice", Ui.Green),
        _ => Ui.Pill("Invoiced", Ui.Muted),
    };

    public static Button NextStepButton(Quotation q)
    {
        var st = AppServices.Current.Store.StageOf(q);
        var (label, primary, tint) = st switch
        {
            OrderStage.NeedsChallan => ("Create DC", false, (Brush?)null),
            OrderStage.AwaitingApproval => ("Approve DC", false, Ui.Orange),
            OrderStage.ReadyToInvoice => ("Create Invoice", true, Ui.Green),
            _ => ("View Invoice", false, null),
        };
        var b = Ui.Btn(label, () => Nav.NextStep(q), primary, tint: tint);
        b.Padding = new Thickness(10, 2, 10, 2);
        b.MinHeight = 26;
        b.Margin = new Thickness(0);
        return b;
    }
}

/// <summary>Type an Item ID or name; matching inventory items drop down. Click or press Enter to use one.</summary>
sealed class ItemSearchBox : Grid
{
    readonly TextBox box = new() { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14 };
    readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    readonly ListBox list = new() { MaxHeight = 360, Width = 560, BorderThickness = new Thickness(1), BorderBrush = Ui.Line };
    readonly bool allowNew;
    readonly Action<Item> onPick;
    readonly Action<string> onNew;
    List<Item> matches = new();

    public ItemSearchBox(string hint, Action<Item> onPick, bool allowNew = false, Action<string>? onNew = null)
    {
        this.onPick = onPick;
        this.allowNew = allowNew;
        this.onNew = onNew ?? (_ => { });
        box.ToolTip = hint;
        var label = Ui.Caption(hint).Also(t => { t.Margin = new Thickness(0, 0, 0, 4); });
        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(box);
        Children.Add(stack);
        popup.PlacementTarget = box;
        popup.Child = new Border { Child = list, Background = Brushes.White, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Opacity = 0.2 } };
        list.PreviewMouseLeftButtonUp += (_, _) => PickSelected();
        box.TextChanged += (_, _) => Update();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && popup.IsOpen) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up && popup.IsOpen) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Escape) popup.IsOpen = false;
            else if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
        };
    }

    public void FocusBox() => box.Focus();

    void Update()
    {
        var q = box.Text.T().ToLowerInvariant();
        list.Items.Clear();
        if (q.Length == 0) { popup.IsOpen = false; return; }
        var items = AppServices.Current.Store.Items;
        matches = items.Where(i => i.Code.ToLowerInvariant().StartsWith(q))
            .Concat(items.Where(i => !i.Code.ToLowerInvariant().StartsWith(q) && (i.Code.ToLowerInvariant().Contains(q) || i.Name.ToLowerInvariant().Contains(q) || i.Details.ToLowerInvariant().Contains(q))))
            .Take(8).ToList();
        foreach (var it in matches) list.Items.Add(Row(it));
        var exact = AppServices.Current.Store.ItemByCode(box.Text) != null;
        if (allowNew && !exact) list.Items.Add(new ListBoxItem { Content = Ui.Row(Ui.Text("➕  ", 13, null, Ui.Green), Ui.Text($"Add new item with ID “{box.Text.T()}”", 13)), Tag = "new", Padding = new Thickness(8) });
        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        popup.IsOpen = list.Items.Count > 0;
    }

    ListBoxItem Row(Item it)
    {
        var cur = AppServices.Current.Store.Settings.Currency;
        var left = Ui.Row(Ui.Thumb(it.Image, 30).Also(t => t.Margin = new Thickness(0, 0, 10, 0)),
            Ui.Col(0, Ui.Row(Ui.Text(it.Code, 13, FontWeights.SemiBold), Ui.Text("  " + it.Name, 13)), Ui.Text(it.Details, 11.5, null, Ui.Muted)));
        var right = Ui.Col(0, Ui.Text(Fmt.Money(it.SalePrice, cur), 13).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right),
            Ui.Text($"Stock: {Fmt.Qty(it.Stock)} {it.Unit}", 11.5, null, it.Stock > 0 ? Ui.Muted : Ui.Red).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
        var dock = new DockPanel { Width = 520 };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return new ListBoxItem { Content = dock, Tag = it, Padding = new Thickness(8, 5, 8, 5) };
    }

    void PickSelected()
    {
        if (list.SelectedItem is not ListBoxItem li) return;
        if (li.Tag is Item it) { onPick(it); Clear(); }
        else if (li.Tag is "new") { onNew(box.Text.T()); Clear(); }
    }

    void Submit()
    {
        var exact = AppServices.Current.Store.ItemByCode(box.Text);
        if (exact != null) { onPick(exact); Clear(); return; }
        if (popup.IsOpen && list.SelectedItem != null) { PickSelected(); return; }
        if (allowNew && box.Text.T().Length > 0) { onNew(box.Text.T()); Clear(); }
    }

    void Clear()
    {
        box.Text = "";
        popup.IsOpen = false;
        box.Focus();
    }
}

static class Flow
{
    /// <summary>Quotation → Delivery Challan → Invoice progress strip.</summary>
    public static StackPanel Steps(bool quotationSaved, Challan? c, Invoice? inv)
    {
        UIElement Step(string title, string icon, bool done, bool pending, string detail)
        {
            var color = done ? Ui.Green : pending ? Ui.Orange : Ui.Muted;
            var mark = Ui.Text(done ? "✔" : pending ? "⏳" : "○", 15, FontWeights.Bold, color).Also(t => t.Margin = new Thickness(0, 0, 8, 0));
            var text = Ui.Col(0, Ui.Text(title, 12, FontWeights.SemiBold), Ui.Text(detail, 11, null, Ui.Muted));
            return new Border { Child = Ui.Row(Ui.Icon(icon, 22).Also(i => i.Margin = new Thickness(0, 0, 8, 0)), mark, text), Background = Ui.Tint(color, 22), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 12, 6) };
        }
        UIElement Arrow() => Ui.Text("  ›  ", 16, FontWeights.Bold, Ui.Muted);
        return Ui.Row(
            Step("Quotation", "quotations", quotationSaved, false, quotationSaved ? "Saved" : "Not saved"), Arrow(),
            Step("Delivery Challan", "challans", c?.Status == ChallanStatus.Approved, c?.Status == ChallanStatus.Pending,
                 c == null ? "Not created" : $"{c.Number} · {(c.Status == ChallanStatus.Approved ? "Approved" : "Pending approval")}"), Arrow(),
            Step("Invoice", "invoices", inv != null, false, inv?.Number ?? "Not created"));
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

enum Section { Dashboard, Quotations, Challans, Invoices, Inventory, Purchases, Reports, Settings }

/// <summary>Base for the pages shown on the right.</summary>
abstract class Page : UserControl
{
    protected static AppServices S => AppServices.Current;
    protected static Store Store => AppServices.Current.Store;
    public abstract void Refresh();
}

/// <summary>Opens editor windows one after another (an editor can ask for the next one).</summary>
static class Nav
{
    public static MainWindow Main => (MainWindow)Application.Current.MainWindow;

    public static void Go(Section s) => Main.Show(s);

    public static void Open(EditorWindow w)
    {
        w.Owner = Application.Current.MainWindow;
        w.ShowDialog();
        var next = w.Next;
        if (next != null) Application.Current.Dispatcher.BeginInvoke(next);
    }

    public static void Preview(PrintableDoc doc)
    {
        var pdf = PdfDocs.Render(doc, AppServices.Current.Store.Settings);
        new PdfPreviewWindow(pdf, doc.FileName.Replace(".pdf", ""), doc.FileName).ShowDialog();
    }

    public static void NewQuotation() => Open(new QuotationWindow(AppServices.Current.Store.DraftQuotation()));
    public static void NewPurchase() { AppServices.Current.ShowCost = true; Open(new PurchaseWindow(new Purchase())); }

    public static void NextStep(Quotation q)
    {
        var st = AppServices.Current.Store;
        switch (st.StageOf(q))
        {
            case OrderStage.NeedsChallan: Open(new ChallanWindow(st.DraftChallan(q))); break;
            case OrderStage.AwaitingApproval: Open(new ChallanWindow(st.ChallanForQuotation(q.Id)!)); break;
            case OrderStage.ReadyToInvoice: Open(new InvoiceWindow(st.DraftInvoice(st.ChallanForQuotation(q.Id)!))); break;
            default: Open(new InvoiceWindow(st.InvoiceForQuotation(q.Id)!)); break;
        }
    }
}

/// <summary>Editor windows: modal, with an optional follow-up window.</summary>
abstract class EditorWindow : Window
{
    public Action? Next { get; protected set; }
    protected static Store Store => AppServices.Current.Store;
    protected static AppServices S => AppServices.Current;

    protected EditorWindow(double width, double height)
    {
        Width = width; Height = height; MinWidth = Math.Min(width, 900); MinHeight = Math.Min(height, 560);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.PageBg;
        ShowInTaskbar = false;
        Theme.Window(this, Ui.CardBg);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { OnSaveShortcut(); e.Handled = true; }
        };
    }

    protected virtual void OnSaveShortcut() { }

    protected void Layout(UIElement header, UIElement body, UIElement footer)
    {
        var root = new DockPanel { Background = Ui.PageBg };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(Ui.Scroll(new Border { Child = body, Padding = new Thickness(24, 20, 24, 24) }));
        Content = root;
    }

    protected void OpenNext(Action next) { Next = next; Close(); }
}

sealed class MainWindow : Window
{
    readonly ContentControl content = new();
    readonly Dictionary<Section, Page> pages = new();
    readonly Dictionary<Section, (Squircle Box, TextBlock Label, TextBlock Badge)> navItems = new();
    readonly TextBlock officeStatus = Ui.Text("", 11.5, null, Theme.SideMuted);
    readonly TextBlock cloudStatus = Ui.Text("", 11.5, null, Theme.SideMuted);
    readonly System.Windows.Shapes.Ellipse officeDot = Dot(), cloudDot = Dot();
    readonly Button costButton = new() { Style = Theme.Button(Look.Glass), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(14, 8, 14, 16) };
    public Section Current { get; private set; } = Section.Dashboard;
    public OrderStage? QuotationFilter { get; set; }

    static AppServices S => AppServices.Current;

    public MainWindow()
    {
        Title = "AFAN Traders";
        Width = 1400; Height = 880; MinWidth = 1100; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.PageBg;
        Theme.Window(this, Ui.PageBg);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }

        var grid = new Grid { Background = Ui.PageBg };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = BuildSidebar();
        grid.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        Content = grid;

        S.Refreshed += RefreshAll;
        S.CostChanged += RefreshAll;
        S.SyncChanged += UpdateStatus;
        InputBindings.Add(new KeyBinding(new Relay(Nav.NewQuotation), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(Nav.NewPurchase), Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new Relay(() => S.ShowCost = !S.ShowCost), Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Show(Section.Dashboard);
        UpdateStatus();
    }

    static System.Windows.Shapes.Ellipse Dot() => new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    UIElement BuildSidebar()
    {
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        // Brand: monogram on an indigo squircle.
        var mark = new Squircle
        {
            Width = 38, Height = 38, CornerRadius = 12, Background = Theme.AccentGradient, Shadow = 14, ShadowDepth = 3, ShadowOpacity = 0.45, ShadowColor = Theme.AccentColor,
            Child = Ui.Text("A", 19, FontWeights.Bold, Brushes.White).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 0, 0, 1); }),
        };
        var brand = Ui.Row(mark, Ui.Col(0, Ui.Text("AFAN Traders", 15.5, FontWeights.Bold, Brushes.White), Ui.Text("Business Suite", 11.5, null, Theme.SideMuted))
            .Also(c => c.Margin = new Thickness(11, 0, 0, 0)));
        brand.Margin = new Thickness(20, 14, 0, 18);
        list.Children.Add(brand);
        void Group(string title, params (Section S, string Label, string Icon)[] items)
        {
            list.Children.Add(Ui.Text(title.ToUpperInvariant(), 10.5, FontWeights.SemiBold, Theme.SideMuted).Also(t => t.Margin = new Thickness(24, 12, 0, 6)));
            foreach (var (s, label, icon) in items) list.Children.Add(NavItem(s, label, icon));
        }
        Group("Sales", (Section.Dashboard, "Dashboard", "dashboard"), (Section.Quotations, "Quotations", "quotations"),
              (Section.Challans, "Delivery Challans", "challans"), (Section.Invoices, "Invoices", "invoices"));
        Group("Stock", (Section.Inventory, "Inventory", "inventory"), (Section.Purchases, "Purchase Invoices", "purchases"));
        Group("Analytics", (Section.Reports, "Reports", "reports"));
        Group("System", (Section.Settings, "Settings", "settings"));

        var bottom = new StackPanel();
        bottom.Children.Add(new Border { Height = 1, Background = Ui.Hex("#1FFFFFFF"), Margin = new Thickness(18, 0, 18, 10) });
        bottom.Children.Add(new Border { Child = Ui.Row(officeDot, officeStatus), Padding = new Thickness(24, 3, 12, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent }.Also(b => b.MouseLeftButtonUp += (_, _) => Show(Section.Settings)));
        bottom.Children.Add(new Border { Child = Ui.Row(cloudDot, cloudStatus), Padding = new Thickness(24, 3, 12, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent }.Also(b => b.MouseLeftButtonUp += (_, _) => Show(Section.Settings)));
        costButton.Click += (_, _) => S.ShowCost = !S.ShowCost;
        costButton.ToolTip = "Show or hide cost prices (Ctrl+Shift+K)";
        bottom.Children.Add(costButton);

        var dock = new DockPanel { Background = Theme.SideBg };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(Ui.Scroll(list));
        return dock;
    }

    UIElement NavItem(Section s, string label, string icon)
    {
        var text = Ui.Text(label, 13.5, FontWeights.Medium, Theme.SideText);
        var badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(7, 1, 7, 1) };
        var badgeBox = new Border { Background = Ui.Hex("#F59E0B"), CornerRadius = new CornerRadius(9), Child = badge, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var dock = new DockPanel();
        DockPanel.SetDock(badgeBox, Dock.Right);
        dock.Children.Add(badgeBox);
        dock.Children.Add(Ui.Row(Ui.Icon(icon, 22).Also(i => i.Margin = new Thickness(0, 0, 11, 0)), text));
        var box = new Squircle { Child = dock, Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(12, 1, 12, 1), CornerRadius = 12, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        box.MouseLeftButtonUp += (_, _) => Show(s);
        box.MouseEnter += (_, _) => { if (Current != s) box.Background = Theme.SideHover; };
        box.MouseLeave += (_, _) => { if (Current != s) box.Background = Brushes.Transparent; };
        navItems[s] = (box, text, badge);
        badge.Tag = badgeBox;
        return box;
    }

    public void Show(Section s)
    {
        Current = s;
        if (!pages.TryGetValue(s, out var page))
        {
            page = s switch
            {
                Section.Dashboard => new DashboardPage(),
                Section.Quotations => new QuotationsPage(),
                Section.Challans => new ChallansPage(),
                Section.Invoices => new InvoicesPage(),
                Section.Inventory => new InventoryPage(),
                Section.Purchases => new PurchasesPage(),
                Section.Reports => new ReportsPage(),
                _ => new SettingsPage(),
            };
            pages[s] = page;
        }
        content.Content = page;
        page.Refresh();
        foreach (var (key, (box, label, _)) in navItems)
        {
            var on = key == s;
            box.Background = on ? Theme.AccentGradient : Brushes.Transparent;
            box.Shadow = on ? 16 : 0; box.ShadowDepth = 4; box.ShadowOpacity = 0.5; box.ShadowColor = Theme.AccentColor;
            label.Foreground = on ? Brushes.White : Theme.SideText;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
        }
        UpdateBadges();
    }

    void RefreshAll()
    {
        if (content.Content is Page p) p.Refresh();
        UpdateBadges();
        UpdateStatus();
    }

    void UpdateBadges()
    {
        void Badge(Section s, int n)
        {
            var (_, _, badge) = navItems[s];
            badge.Text = n.ToString();
            ((Border)badge.Tag).Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Badge(Section.Quotations, S.Store.Quotations.Count(q => S.Store.StageOf(q) != OrderStage.Invoiced));
        Badge(Section.Challans, S.Store.Challans.Count(c => c.Status == ChallanStatus.Pending));
    }

    void UpdateStatus()
    {
        var lanOn = S.Lan.Enabled;
        var connected = S.Lan.Peers.Count(p => p.Connected);
        officeStatus.Text = lanOn ? (connected > 0 ? $"Office: {connected} computer{(connected == 1 ? "" : "s")} connected" : "Office: looking for computers…") : "";
        officeStatus.Parent.SetValue(VisibilityProperty, lanOn ? Visibility.Visible : Visibility.Collapsed);
        officeDot.Fill = connected > 0 ? Theme.SideGreen : Theme.SideAmber;
        cloudStatus.Text = S.Cloud.Settings.Enabled ? S.Cloud.StatusText : "Cloud sync off";
        cloudDot.Fill = !S.Cloud.Settings.Enabled ? Theme.SideMuted : S.Cloud.State switch { CloudSync.Phase.Error => Theme.SideRed, CloudSync.Phase.Offline => Theme.SideAmber, _ => Theme.SideGreen };
        costButton.Content = Ui.Row(Ui.Icon("eye", 18), Ui.Text(S.ShowCost ? "   Cost prices visible" : "   Cost prices hidden", 12.5, FontWeights.SemiBold, S.ShowCost ? Theme.SideAmber : Theme.SideText));
    }
}

sealed class Relay : ICommand
{
    readonly Action a;
    public Relay(Action a) => this.a = a;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => Ui.Safe(a);
}

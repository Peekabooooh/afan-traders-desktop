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
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { OnSaveShortcut(); e.Handled = true; }
        };
    }

    protected virtual void OnSaveShortcut() { }

    protected void Layout(UIElement header, UIElement body, UIElement footer)
    {
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(Ui.Scroll(new Border { Child = body, Padding = new Thickness(20) }));
        Content = root;
    }

    protected void OpenNext(Action next) { Next = next; Close(); }
}

sealed class MainWindow : Window
{
    readonly ContentControl content = new();
    readonly Dictionary<Section, Page> pages = new();
    readonly Dictionary<Section, (Border Box, TextBlock Label, TextBlock Badge)> navItems = new();
    readonly TextBlock officeStatus = Ui.Text("", 11.5, null, Ui.Muted);
    readonly TextBlock cloudStatus = Ui.Text("", 11.5, null, Ui.Muted);
    readonly Button costButton = new() { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(10, 4, 10, 12) };
    public Section Current { get; private set; } = Section.Dashboard;
    public OrderStage? QuotationFilter { get; set; }

    static AppServices S => AppServices.Current;

    public MainWindow()
    {
        Title = "AFAN Traders";
        Width = 1400; Height = 880; MinWidth = 1100; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.PageBg;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
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

    UIElement BuildSidebar()
    {
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        list.Children.Add(Ui.Row(Ui.Icon("quotations", 30), Ui.Text("  AFAN Traders", 16, FontWeights.Bold, Ui.Navy)).Also(r => r.Margin = new Thickness(16, 12, 0, 14)));
        void Group(string title, params (Section S, string Label, string Icon)[] items)
        {
            list.Children.Add(Ui.Text(title, 11.5, FontWeights.SemiBold, Ui.Muted).Also(t => t.Margin = new Thickness(18, 10, 0, 4)));
            foreach (var (s, label, icon) in items) list.Children.Add(NavItem(s, label, icon));
        }
        Group("Sales", (Section.Dashboard, "Dashboard", "dashboard"), (Section.Quotations, "Quotations", "quotations"),
              (Section.Challans, "Delivery Challans", "challans"), (Section.Invoices, "Invoices", "invoices"));
        Group("Stock", (Section.Inventory, "Inventory", "inventory"), (Section.Purchases, "Purchase Invoices", "purchases"));
        Group("Analytics", (Section.Reports, "Reports", "reports"));
        Group("System", (Section.Settings, "Settings", "settings"));

        var bottom = new StackPanel();
        bottom.Children.Add(new Border { Child = officeStatus, Padding = new Thickness(18, 4, 10, 2), Cursor = Cursors.Hand }.Also(b => b.MouseLeftButtonUp += (_, _) => Show(Section.Settings)));
        bottom.Children.Add(new Border { Child = cloudStatus, Padding = new Thickness(18, 2, 10, 4), Cursor = Cursors.Hand }.Also(b => b.MouseLeftButtonUp += (_, _) => Show(Section.Settings)));
        costButton.Click += (_, _) => S.ShowCost = !S.ShowCost;
        costButton.ToolTip = "Show or hide cost prices (Ctrl+Shift+K)";
        bottom.Children.Add(costButton);

        var dock = new DockPanel { Background = Ui.Hex("#EEF1F5") };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(Ui.Scroll(list));
        return new Border { Child = dock, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
    }

    UIElement NavItem(Section s, string label, string icon)
    {
        var text = Ui.Text(label, 13.5);
        var badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(6, 0, 6, 0) };
        var badgeBox = new Border { Background = Ui.Orange, CornerRadius = new CornerRadius(9), Child = badge, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var dock = new DockPanel();
        DockPanel.SetDock(badgeBox, Dock.Right);
        dock.Children.Add(badgeBox);
        dock.Children.Add(Ui.Row(Ui.Icon(icon, 22).Also(i => i.Margin = new Thickness(0, 0, 10, 0)), text));
        var box = new Border { Child = dock, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(10, 1, 10, 1), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        box.MouseLeftButtonUp += (_, _) => Show(s);
        box.MouseEnter += (_, _) => { if (Current != s) box.Background = Ui.Tint(Ui.Navy, 18); };
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
            box.Background = on ? Ui.Blue : Brushes.Transparent;
            label.Foreground = on ? Brushes.White : Brushes.Black;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
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
        officeStatus.Text = lanOn ? (connected > 0 ? $"🖧  Office: {connected} computer{(connected == 1 ? "" : "s")} connected" : "🖧  Office: looking for computers…") : "";
        officeStatus.Visibility = lanOn ? Visibility.Visible : Visibility.Collapsed;
        officeStatus.Foreground = connected > 0 ? Ui.Green : Ui.Muted;
        cloudStatus.Text = S.Cloud.Settings.Enabled ? "☁  " + S.Cloud.StatusText : "☁  Cloud sync off";
        cloudStatus.Foreground = S.Cloud.State switch { CloudSync.Phase.Error => Ui.Red, CloudSync.Phase.Offline => Ui.Orange, _ => Ui.Muted };
        costButton.Content = Ui.Row(Ui.Icon("eye", 18), Ui.Text(S.ShowCost ? "  Cost prices visible" : "  Cost prices hidden", 12, FontWeights.Medium, S.ShowCost ? Ui.Orange : Ui.Muted));
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

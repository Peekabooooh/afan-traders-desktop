using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

static class Forms
{
    public static Border Notes(string label, string text, Action<string> changed) =>
        new() { Child = Ui.Field(label, Ui.Input(text, changed, multiline: true).Also(t => t.Height = 90)), MinWidth = 380 };

    public static Squircle CustomerCard(string title, string name, string address, string phone,
                                      Action<string> setName, Action<string> setAddress, Action<string> setPhone, bool offerPrevious)
    {
        var nameBox = Ui.Input(name, setName);
        var addrBox = Ui.Input(address, setAddress, multiline: true).Also(t => t.Height = 56);
        var phoneBox = Ui.Input(phone, setPhone);
        var header = new DockPanel();
        header.Children.Add(Ui.Heading(title));
        if (offerPrevious)
        {
            var customers = AppServices.Current.Store.KnownCustomers();
            if (customers.Count > 0)
            {
                var pick = new ComboBox { Width = 210, MinHeight = 34, ToolTip = "Previous customers" };
                pick.Items.Add(new ComboBoxItem { Content = "Previous customers…", IsEnabled = false });
                foreach (var c in customers) pick.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c });
                pick.SelectedIndex = 0;
                pick.SelectionChanged += (_, _) =>
                {
                    if (pick.SelectedItem is ComboBoxItem { Tag: ValueTuple<string, string, string> c })
                    { nameBox.Text = c.Item1; addrBox.Text = c.Item2; phoneBox.Text = c.Item3; }
                };
                DockPanel.SetDock(pick, Dock.Right);
                header.Children.Insert(0, pick);
            }
        }
        return Ui.Card(Ui.Col(10, header, Ui.Field("Customer / Company name", nameBox), Ui.Field("Address", addrBox), Ui.Field("Phone", phoneBox)));
    }

    public static StackPanel NumberField(string label, string value, Action<string> changed, Func<string> regenerate)
    {
        var box = Ui.Input(value, changed, width: 230);
        var regen = Ui.Btn("↻", () => box.Text = regenerate(), tooltip: "Generate a new number");
        return Ui.Field(label, Ui.Row(box, regen));
    }

    public static Grid TwoColumns(UIElement left, UIElement right, double rightWidth = 420)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWidth) });
        g.Children.Add(left);
        Grid.SetColumn((UIElement)right, 2);
        g.Children.Add(right);
        return g;
    }

    public static Squircle Banner(string text, Brush color) => Ui.Note(Ui.Text(text, 13, FontWeights.Medium, color, TextWrapping.Wrap), color);

    public static DockPanel Bottom(UIElement left, UIElement right)
    {
        var d = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        d.Children.Add(left);
        return d;
    }
}

sealed class QuotationWindow : EditorWindow
{
    Quotation q;
    readonly LinesEditor lines;
    readonly TotalsBox totals;
    Challan? Challan => Store.ChallanForQuotation(q.Id);
    bool Locked => Challan != null;

    public QuotationWindow(Quotation quote) : base(1180, 860)
    {
        q = quote;
        Title = q.Id == 0 ? "New Quotation" : $"Quotation {q.Number}";
        lines = new LinesEditor(q.Lines, LinesEditor.Mode.Quotation, Locked);
        totals = new TotalsBox(() => (Fmt.R2(lines.Lines.Sum(l => l.Total)), q.TaxRate, Store.Profit(lines.Lines)), q.TaxRate, Locked ? null : v => q = q with { TaxRate = v });
        lines.Changed += totals.Update;

        var details = Ui.Card(Ui.Col(10,
            Ui.Heading("Quotation Details"),
            Forms.NumberField("Quotation No.", q.Number, v => q = q with { Number = v }, Store.NewQuotationNumber),
            Ui.Row(Ui.Field("Date", Ui.Date(q.Date, v => q = q with { Date = v })), new Border { Width = 12 },
                   Ui.Field("PO Number", Ui.Input(q.PoNumber, v => q = q with { PoNumber = v }, width: 180))),
            Ui.Caption("The PO number links this quotation, its delivery challan and its invoice.")));
        var customer = Forms.CustomerCard("Customer", q.CustomerName, q.CustomerAddress, q.CustomerPhone,
            v => q = q with { CustomerName = v }, v => q = q with { CustomerAddress = v }, v => q = q with { CustomerPhone = v }, offerPrevious: true);

        var body = new StackPanel();
        body.Children.Add(Flow.Steps(q.Id != 0, Challan, Store.InvoiceForQuotation(q.Id)));
        if (Locked) body.Children.Add(Forms.Banner("🔒 Items are locked because a delivery challan has been created. You can still change the customer and PO number.", Ui.Orange).Also(b => b.Margin = new Thickness(0, 12, 0, 0)));
        body.Children.Add(Forms.TwoColumns(customer, details).Also(g => g.Margin = new Thickness(0, 14, 0, 14)));
        if (!Locked) body.Children.Add(new ItemSearchBox("Type Item ID or name to add an item…", lines.Add).Also(s => s.Margin = new Thickness(0, 0, 0, 10)));
        body.Children.Add(lines);
        body.Children.Add(Forms.Bottom(Forms.Notes("Notes (printed on the quotation)", q.Notes, v => q = q with { Notes = v }), totals).Also(d => d.Margin = new Thickness(0, 14, 0, 0)));

        var left = new List<UIElement>();
        if (q.Id != 0 && !Locked) left.Add(Ui.Btn("Delete", Delete, tint: Ui.Red));
        var right = new List<UIElement> { Ui.Btn("Preview / Print", () => Nav.Preview(Store.Printable(Current()))) };
        if (Challan is { } ch) right.Add(Ui.Btn("Open Delivery Challan", () => OpenNext(() => Nav.Open(new ChallanWindow(Store.ChallanById(ch.Id)!))), icon: "challans"));
        else right.Add(Ui.Btn("Save & Create Delivery Challan", SaveThenChallan, icon: "challans"));
        right.Add(Ui.Btn("Cancel", Close));
        right.Add(Ui.Btn("Save", () => { Save(); Close(); }, primary: true));
        Layout(Ui.Header(Title, q.CustomerName.Length == 0 ? "Fill in the customer and add items" : q.CustomerName, "quotations"), body, Ui.Footer(left, right));
    }

    Quotation Current() => q with { Lines = lines.Lines.ToList() };
    Quotation Save() => q = Store.SaveQuotation(Current());
    protected override void OnSaveShortcut() => Ui.Safe(() => { Save(); Close(); });

    void SaveThenChallan()
    {
        var saved = Save();
        OpenNext(() => Nav.Open(new ChallanWindow(Store.DraftChallan(saved))));
    }

    void Delete()
    {
        if (!Ui.Confirm($"Delete quotation {q.Number}?")) return;
        Store.DeleteQuotation(q);
        Close();
    }
}

sealed class ChallanWindow : EditorWindow
{
    Challan c;
    Challan? Saved => c.Id == 0 ? null : Store.ChallanById(c.Id);
    bool Approved => Saved?.Status == ChallanStatus.Approved;
    Invoice? Inv => Store.InvoiceForChallan(c.Id);

    public ChallanWindow(Challan challan) : base(1120, 820)
    {
        c = challan;
        Title = c.Id == 0 ? "New Delivery Challan" : $"Delivery Challan {c.Number}";
        Build();
    }

    void Build()
    {
        var q = Store.QuotationById(c.QuotationId);
        var customer = Forms.CustomerCard("Deliver To", c.CustomerName, c.CustomerAddress, c.CustomerPhone,
            v => c = c with { CustomerName = v }, v => c = c with { CustomerAddress = v }, v => c = c with { CustomerPhone = v }, offerPrevious: false);
        var details = Ui.Card(Ui.Col(10,
            Ui.Heading("Challan Details"),
            Forms.NumberField("Delivery Challan No.", c.Number, v => c = c with { Number = v }, Store.NewChallanNumber),
            Ui.Row(Ui.Field("Date", Ui.Date(c.Date, v => c = c with { Date = v })), new Border { Width = 12 },
                   Ui.Field("PO Number", Ui.Text(c.PoNumber.Length == 0 ? "—" : c.PoNumber, 13, FontWeights.SemiBold)), new Border { Width = 18 },
                   Ui.Field("Quotation", Ui.Text(q?.Number ?? "—", 13, FontWeights.SemiBold))),
            Ui.Row(Ui.Field("Vehicle No.", Ui.Input(c.VehicleNo, v => c = c with { VehicleNo = v }, width: 170)), new Border { Width = 12 },
                   Ui.Field("Driver name", Ui.Input(c.DriverName, v => c = c with { DriverName = v }, width: 190)))));

        var body = new StackPanel();
        body.Children.Add(Flow.Steps(true, Saved, Inv));
        body.Children.Add(Forms.TwoColumns(customer, details, 440).Also(g => g.Margin = new Thickness(0, 14, 0, 14)));
        body.Children.Add(new LinesEditor(c.Lines, LinesEditor.Mode.Challan, locked: true));
        var qty = Ui.Card(Ui.Col(4, Ui.Caption("Total quantity"), Ui.Text(Fmt.Qty(c.TotalQty), 22, FontWeights.Bold)));
        body.Children.Add(Forms.Bottom(Forms.Notes("Notes (printed on the challan)", c.Notes, v => c = c with { Notes = v }), qty).Also(d => d.Margin = new Thickness(0, 14, 0, 0)));
        if (!Approved) body.Children.Add(Ui.Caption("ℹ Approving the challan removes these quantities from stock.").Also(t => t.Margin = new Thickness(0, 8, 0, 0)));

        var left = new List<UIElement>();
        if (c.Id != 0 && Inv == null) left.Add(Ui.Btn("Delete", Delete, tint: Ui.Red));
        if (Saved is { } s) left.Add(Ui.Pill(s.Status == ChallanStatus.Approved ? "✔ Approved" : "⏳ Pending approval", s.Status == ChallanStatus.Approved ? Ui.Green : Ui.Orange).Also(p => p.Margin = new Thickness(10, 0, 0, 0)));
        var right = new List<UIElement> { Ui.Btn("Preview / Print", () => Nav.Preview(Store.Printable(c))) };
        if (c.Id != 0 && Approved && Inv == null) right.Add(Ui.Btn("Mark as Pending", () => { Store.SetApproved(c, false); Reload(); }));
        if (!Approved) right.Add(Ui.Btn("Save & Approve", Approve, tint: Ui.Green, icon: "ready"));
        if (Inv is { } inv) right.Add(Ui.Btn("Open Invoice", () => OpenNext(() => Nav.Open(new InvoiceWindow(inv))), icon: "invoices"));
        else if (Approved) right.Add(Ui.Btn("Create Invoice", CreateInvoice, primary: true, tint: Ui.Green, icon: "invoices"));
        right.Add(Ui.Btn("Cancel", Close));
        right.Add(Ui.Btn("Save", () => { Save(); Close(); }, primary: true));
        Layout(Ui.Header(Title, "For the fleet / gate: the material allowed to enter. No prices are shown.", "challans"), body, Ui.Footer(left, right));
    }

    void Reload() { c = Store.ChallanById(c.Id) ?? c; Build(); }
    Challan Save() => c = Store.SaveChallan(c);
    protected override void OnSaveShortcut() => Ui.Safe(() => { Save(); Close(); });

    void Approve()
    {
        var shortages = Store.Shortages(c);
        if (shortages.Count > 0 && !Ui.Confirm(string.Join("\n", shortages) + "\n\nStock will go below zero. Approve anyway?", "Not enough stock")) return;
        var s = Save();
        Store.SetApproved(s, true);
        Reload();
    }

    void CreateInvoice()
    {
        var s = Save();
        var draft = Store.DraftInvoice(s);
        OpenNext(() => Nav.Open(new InvoiceWindow(draft)));
    }

    void Delete()
    {
        if (!Ui.Confirm($"Delete delivery challan {c.Number}?" + (Approved ? "\nThe quantities will be put back into stock." : ""))) return;
        Store.DeleteChallan(c);
        Close();
    }
}

sealed class InvoiceWindow : EditorWindow
{
    Invoice inv;
    readonly LinesEditor lines;
    readonly TotalsBox totals;
    readonly TextBlock diff = Ui.Text("", 13, FontWeights.SemiBold);
    double margin;

    public InvoiceWindow(Invoice invoice) : base(1180, 860)
    {
        inv = invoice;
        Title = inv.Id == 0 ? "New Invoice" : $"Invoice {inv.Number}";
        var q = Store.QuotationById(inv.QuotationId);
        var ch = Store.ChallanById(inv.ChallanId);
        lines = new LinesEditor(inv.Lines, LinesEditor.Mode.Invoice);
        totals = new TotalsBox(() => (Fmt.R2(lines.Lines.Sum(l => l.Total)), inv.TaxRate, Store.Profit(lines.Lines)), inv.TaxRate, v => inv = inv with { TaxRate = v });
        lines.Changed += () => { totals.Update(); UpdateDiff(); };

        var customer = Forms.CustomerCard("Bill To", inv.CustomerName, inv.CustomerAddress, inv.CustomerPhone,
            v => inv = inv with { CustomerName = v }, v => inv = inv with { CustomerAddress = v }, v => inv = inv with { CustomerPhone = v }, offerPrevious: false);
        UIElement Ref(string label, string value) => Ui.Field(label, Ui.Text(value.Length == 0 ? "—" : value, 13, FontWeights.SemiBold)).Also(f => f.Margin = new Thickness(0, 0, 18, 0));
        var details = Ui.Card(Ui.Col(10,
            Ui.Heading("Invoice Details"),
            Forms.NumberField("Invoice No.", inv.Number, v => inv = inv with { Number = v }, Store.NewInvoiceNumber),
            Ui.Field("Date", Ui.Date(inv.Date, v => inv = inv with { Date = v })),
            Ui.Row(Ref("PO Number", inv.PoNumber), Ref("Quotation", q?.Number ?? ""), Ref("Delivery Challan", ch?.Number ?? ""))));

        var marginTool = Ui.Card(Forms.Bottom(
            Ui.Row(Ui.Icon("money", 22), Ui.Text("  Add margin on quotation prices: ", 13), Ui.Number(0, v => margin = v, 70), Ui.Text(" %  ", 13),
                   Ui.Btn("Apply to all items", () => lines.SetRates(l => l.BaseRate * (1 + margin / 100))),
                   Ui.Btn("Reset to quotation prices", () => lines.SetRates(l => l.BaseRate))),
            diff), 10);

        var body = new StackPanel();
        body.Children.Add(Flow.Steps(true, ch, inv.Id == 0 ? null : inv));
        body.Children.Add(Forms.TwoColumns(customer, details, 460).Also(g => g.Margin = new Thickness(0, 14, 0, 12)));
        body.Children.Add(marginTool.Also(m => m.Margin = new Thickness(0, 0, 0, 12)));
        body.Children.Add(lines);
        body.Children.Add(Forms.Bottom(Forms.Notes("Notes (printed on the invoice)", inv.Notes, v => inv = inv with { Notes = v }), totals).Also(d => d.Margin = new Thickness(0, 14, 0, 0)));
        UpdateDiff();

        var left = new List<UIElement>();
        if (inv.Id != 0) left.Add(Ui.Btn("Delete", Delete, tint: Ui.Red));
        var right = new List<UIElement>
        {
            Ui.Btn("Preview / Print", () => Nav.Preview(Store.Printable(Current()))),
            Ui.Btn("Cancel", Close),
            Ui.Btn(inv.Id == 0 ? "Create Invoice" : "Save", () => { Save(); Close(); }, primary: true),
        };
        Layout(Ui.Header(Title, "Prices start from the quotation – change any rate to add your margin.", "invoices"), body, Ui.Footer(left, right));
    }

    void UpdateDiff()
    {
        var quoted = Fmt.R2(lines.Lines.Sum(l => l.BaseRate * l.Qty));
        var d = Fmt.R2(lines.Lines.Sum(l => l.Total) - quoted);
        diff.Text = d == 0 ? "" : $"{(d > 0 ? "+" : "")}{Fmt.Money(d, Store.Settings.Currency)} vs quotation";
        diff.Foreground = d > 0 ? Ui.Green : Ui.Red;
    }

    Invoice Current() => inv with { Lines = lines.Lines.ToList() };
    void Save() => inv = Store.SaveInvoice(Current());
    protected override void OnSaveShortcut() => Ui.Safe(() => { Save(); Close(); });

    void Delete()
    {
        if (!Ui.Confirm($"Delete invoice {inv.Number}?")) return;
        Store.DeleteInvoice(inv);
        Close();
    }
}

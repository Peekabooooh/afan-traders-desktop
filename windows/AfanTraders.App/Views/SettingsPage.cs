using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>Settings save automatically (pictures at once, typing a moment after you stop).</summary>
sealed class SettingsPage : Page
{
    CompanySettings s = new();
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly TextBlock saved = Ui.Text("✔ All changes saved", 13, FontWeights.SemiBold, Ui.Green);
    readonly ContentControl form = new();
    readonly StackPanel officeBox = new(), cloudBox = new();
    bool loaded;

    public SettingsPage()
    {
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        S.SyncChanged += () => { if (IsVisible) { BuildOffice(); BuildCloud(); } };
        var top = PageParts.TitleBar("Settings", null, saved);
        Content = PageParts.Layout(top, Ui.Scroll(form));
        Unloaded += (_, _) => { if (saveTimer.IsEnabled) { saveTimer.Stop(); Save(); } };
    }

    public override void Refresh()
    {
        // Rebuild only the first time, or when settings changed on another computer –
        // never because of our own save (that would move the cursor out of the box being typed in).
        if (!loaded) { s = Store.Settings; loaded = true; Rebuild(); return; }
        if (!saveTimer.IsEnabled && !s.SameAs(Store.Settings)) { s = Store.Settings; Rebuild(); }
    }

    void Rebuild() => form.Content = Build();

    void Change(Func<CompanySettings, CompanySettings> f, bool now = false)
    {
        s = f(s);
        saved.Text = "Saving…"; saved.Foreground = Ui.Muted;
        saveTimer.Stop();
        if (now) Save(); else saveTimer.Start();
    }

    void Save()
    {
        try
        {
            Store.SaveSettings(s);
            saved.Text = "✔ All changes saved"; saved.Foreground = Ui.Green;
        }
        catch (Exception e) { Ui.Message("Could not save settings: " + e.Message); }
    }

    static Border Section(string title, string icon, string? footer, params UIElement[] rows)
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Row(Ui.Icon(icon, 24), Ui.Text("  " + title, 16, FontWeights.SemiBold)).Also(r => r.Margin = new Thickness(0, 0, 0, 10)));
        foreach (var r in rows) p.Children.Add(r.Also(x => { if (x is FrameworkElement f) f.Margin = new Thickness(0, 0, 0, 10); }));
        if (footer != null) p.Children.Add(Ui.Text(footer, 12, null, Ui.Muted, TextWrapping.Wrap));
        return Ui.Card(p).Also(c => { c.Margin = new Thickness(0, 0, 0, 16); c.MaxWidth = 900; c.HorizontalAlignment = HorizontalAlignment.Left; });
    }

    static UIElement Pair(string label, UIElement control)
    {
        var d = new DockPanel();
        var l = Ui.Text(label, 13).Also(t => { t.Width = 230; t.VerticalAlignment = VerticalAlignment.Top; t.Margin = new Thickness(0, 6, 0, 0); });
        DockPanel.SetDock(l, Dock.Left);
        d.Children.Add(l);
        d.Children.Add(control);
        return d;
    }

    UIElement Build()
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        p.Children.Add(Section("Company", "customer", "Used on printed documents when no letterhead is set.",
            Pair("Company name", Ui.Input(s.CompanyName, v => Change(x => x with { CompanyName = v }))),
            Pair("Tagline", Ui.Input(s.Tagline, v => Change(x => x with { Tagline = v }))),
            Pair("Address", Ui.Input(s.Address, v => Change(x => x with { Address = v }), multiline: true)),
            Pair("Phone", Ui.Input(s.Phone, v => Change(x => x with { Phone = v }))),
            Pair("Email", Ui.Input(s.Email, v => Change(x => x with { Email = v }))),
            Pair("NTN", Ui.Input(s.Ntn, v => Change(x => x with { Ntn = v }))),
            Pair("STRN (Sales Tax Reg. No.)", Ui.Input(s.Strn, v => Change(x => x with { Strn = v }))),
            Pair("Logo", Ui.Row(Ui.Thumb(s.Logo, 44),
                Ui.Btn(s.Logo == null ? "Choose…" : "Change…", () => { if (Images.Pick(800, keepTransparency: true) is { } d) { Change(x => x with { Logo = d }, now: true); Rebuild(); } }),
                Ui.Btn("Remove", () => { Change(x => x with { Logo = null }, now: true); Rebuild(); })))));

        var lhRows = new List<UIElement>
        {
            Pair("Letterhead", Ui.Row(
                s.Letterhead != null ? Ui.Text("✔ Letterhead added   ", 13, FontWeights.SemiBold, Ui.Green) : Ui.Text("Not set – the built-in header is used   ", 13, null, Ui.Muted),
                Ui.Btn(s.Letterhead == null ? "Choose PDF or Image…" : "Change…", PickLetterhead),
                Ui.Btn("Remove", () => { Change(x => x with { Letterhead = null }, now: true); Rebuild(); }))),
        };
        if (s.Letterhead != null)
        {
            lhRows.Add(Pair("Space for header (top)", Slider(s.LetterheadTop, 20, 320, v => Change(x => x with { LetterheadTop = v }))));
            lhRows.Add(Pair("Space for footer (bottom)", Slider(s.LetterheadBottom, 10, 240, v => Change(x => x with { LetterheadBottom = v }))));
        }
        lhRows.Add(Pair("", Ui.Btn("Preview Sample Invoice", () =>
        {
            var pdf = PdfDocs.Render(PrintableDoc.Sample(PrintableDoc.DocKind.Invoice, s), s);
            new PdfPreviewWindow(pdf, "Sample invoice", "Sample invoice.pdf").ShowDialog();
        }, icon: "eye").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        p.Children.Add(Section("Letterhead", "printer", "Pick your letterhead (PDF, JPG or PNG). Quotations, delivery challans and invoices will print on it. Adjust the spaces so text doesn't overlap your header and footer.", lhRows.ToArray()));

        p.Children.Add(Section("Tax & Currency", "money", "New quotations start with this tax rate. You can still change it on each quotation and invoice.",
            Pair("Default sales tax", Ui.Row(Ui.Number(s.DefaultTaxRate, v => Change(x => x with { DefaultTaxRate = v }), 80), Ui.Text("  %", 13))),
            Pair("Currency symbol", Ui.Input(s.Currency, v => Change(x => x with { Currency = v }), width: 120)),
            Pair("Currency in words", Ui.Input(s.CurrencyWord, v => Change(x => x with { CurrencyWord = v }), width: 200))));

        p.Children.Add(Section("Document Numbers", "quotations", $"Numbers are generated like {s.QuotationPrefix}-{DateTime.Now:yy}-48213 and can be changed on every document.",
            Pair("Quotation prefix", Ui.Input(s.QuotationPrefix, v => Change(x => x with { QuotationPrefix = v }), width: 120)),
            Pair("Delivery challan prefix", Ui.Input(s.ChallanPrefix, v => Change(x => x with { ChallanPrefix = v }), width: 120)),
            Pair("Invoice prefix", Ui.Input(s.InvoicePrefix, v => Change(x => x with { InvoicePrefix = v }), width: 120)),
            Pair("Invoice title", Ui.Input(s.InvoiceTitle, v => Change(x => x with { InvoiceTitle = v }), width: 260))));

        p.Children.Add(Section("Terms & Conditions", "quotations", null,
            Pair("Quotation terms", Ui.Input(s.QuotationTerms, v => Change(x => x with { QuotationTerms = v }), multiline: true)),
            Pair("Invoice terms", Ui.Input(s.InvoiceTerms, v => Change(x => x with { InvoiceTerms = v }), multiline: true))));

        BuildOffice();
        Detach(officeBox);
        Detach(cloudBox);
        p.Children.Add(Section("Office Network Sync (free – no internet needed)", "dashboard",
            "Computers on the same Wi-Fi or office network – Windows and Mac – find each other and keep the same data, instantly and without any monthly cost. " +
            "Turn it on on every computer with the same office code (at least 4 characters). If Windows asks whether AFAN Traders may use the network, choose Private networks and click Allow.",
            officeBox));
        BuildCloud();
        p.Children.Add(Section("Cloud Sync (Supabase)", "stockin",
            "Optional – for a computer outside the office. 1) Create a free project at supabase.com. 2) Click “Copy Setup SQL”, open SQL Editor in Supabase, paste and Run. " +
            "3) In Supabase → Authentication → Users, add a user. 4) Copy the Project URL and anon key from Project Settings → API into the fields above. The app keeps working without internet.",
            cloudBox));

        p.Children.Add(Section("Data & Backup", "settings", null,
            Pair("Data folder", Ui.Row(Ui.Text(Store.DataFolder + "   ", 12, null, Ui.Muted), Ui.Btn("Open Folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Store.DataFolder}\"") { UseShellExecute = true })))),
            Pair("Backup", Ui.Btn("Save a Backup Copy…", Backup).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left))));
        return p;
    }

    static void Detach(FrameworkElement e)
    {
        if (e.Parent is Panel panel) panel.Children.Remove(e);
    }

    UIElement Slider(double value, double min, double max, Action<double> changed)
    {
        var label = Ui.Text($"{value:0} pt", 13).Also(t => { t.Width = 60; t.Margin = new Thickness(10, 0, 0, 0); });
        var sl = new Slider { Minimum = min, Maximum = max, Value = value, Width = 360, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => { label.Text = $"{e.NewValue:0} pt"; changed(e.NewValue); };
        return Ui.Row(sl, label);
    }

    void PickLetterhead()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Letterhead (PDF or picture)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        var data = File.ReadAllBytes(dlg.FileName);
        if (Letterhead.From(data) == null) { Ui.Message("That file could not be read as a letterhead."); return; }
        Change(x => x with { Letterhead = data }, now: true);
        Rebuild();
    }

    void Backup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"AFAN Traders Backup {Fmt.Date(DateTime.Now)}.sqlite", Filter = "Backup|*.sqlite" };
        if (dlg.ShowDialog() != true) return;
        Store.Backup(dlg.FileName);
        Ui.Message("Backup saved.");
    }

    void BuildOffice()
    {
        var lan = S.Lan;
        officeBox.Children.Clear();
        var color = !lan.Enabled ? Ui.Muted : lan.Problems.Count > 0 ? Ui.Orange : lan.Peers.Any(p => p.Connected) ? Ui.Green : Ui.Muted;
        officeBox.Children.Add(Pair("Status", Ui.Text(lan.StatusText, 13, FontWeights.SemiBold, color)));
        if (lan.Enabled)
        {
            foreach (var peer in lan.Peers.OrderBy(p => p.Name))
                officeBox.Children.Add(Pair(peer.Name, Ui.Text(peer.Connected ? (peer.LastSync is { } t ? $"● Synced at {t:HH:mm:ss}" : "● Connected") : "○ Not reachable right now", 13, null, peer.Connected ? Ui.Green : Ui.Muted)));
            foreach (var pr in lan.Problems.Take(3)) officeBox.Children.Add(Ui.Text(pr, 12.5, null, Ui.Orange, TextWrapping.Wrap));
            officeBox.Children.Add(Pair("", Ui.Btn("Turn Off", lan.TurnOff, tint: Ui.Red).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        }
        else
        {
            var code = new PasswordBox { Width = 240, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            officeBox.Children.Add(Pair("Office code", code));
            officeBox.Children.Add(Pair("", Ui.Btn("Turn On Office Sync", () =>
            {
                if (code.Password.Trim().Length < 4) { Ui.Message("The office code needs at least 4 characters."); return; }
                lan.TurnOn(code.Password);
                BuildOffice();
            }, primary: true).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        }
    }

    void BuildCloud()
    {
        var c = S.Cloud;
        cloudBox.Children.Clear();
        var color = c.State switch { CloudSync.Phase.Error => Ui.Red, CloudSync.Phase.Offline => Ui.Orange, CloudSync.Phase.Idle => c.Pending > 0 ? Ui.Orange : Ui.Green, _ => Ui.Muted };
        cloudBox.Children.Add(Pair("Status", Ui.Text(c.StatusText, 13, FontWeights.SemiBold, color).Also(t => t.TextWrapping = TextWrapping.Wrap)));
        if (c.Settings.Enabled)
        {
            cloudBox.Children.Add(Pair("Project", Ui.Text(c.Settings.Url, 13, null, Ui.Muted)));
            cloudBox.Children.Add(Pair("Signed in as", Ui.Text(c.Settings.Email, 13)));
            cloudBox.Children.Add(Pair("Realtime", Ui.Text(c.Live ? "⚡ Connected – changes from other computers appear instantly" : "Not connected (will retry)", 13, null, c.Live ? Ui.Green : Ui.Muted)));
            foreach (var pr in c.Problems.Take(5)) cloudBox.Children.Add(Ui.Text(pr, 12, null, Ui.Orange, TextWrapping.Wrap));
            cloudBox.Children.Add(Pair("", Ui.Row(Ui.Btn("Sync Now", c.SyncNow), Ui.Btn("Disconnect", c.Disconnect, tint: Ui.Red))));
        }
        else
        {
            var url = Ui.Input(c.Settings.Url, _ => { });
            var key = Ui.Input(c.Settings.AnonKey, _ => { });
            var email = Ui.Input(c.Settings.Email, _ => { });
            var pass = new PasswordBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            var error = Ui.Text("", 12.5, null, Ui.Red, TextWrapping.Wrap);
            Button? connect = null;
            connect = Ui.Btn("Connect & Sign In", async () =>
            {
                connect!.IsEnabled = false;
                error.Text = "";
                try { await c.Connect(url.Text, key.Text, email.Text, pass.Password); pass.Password = ""; }
                catch (Exception e) { error.Text = e.Message; }
                connect.IsEnabled = true;
                BuildCloud();
            }, primary: true);
            cloudBox.Children.Add(Pair("Project URL", url));
            cloudBox.Children.Add(Pair("Anon public key", key));
            cloudBox.Children.Add(Pair("Email", email));
            cloudBox.Children.Add(Pair("Password", pass));
            cloudBox.Children.Add(error);
            cloudBox.Children.Add(Pair("", connect.Also(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        }
        cloudBox.Children.Add(Pair("Setup", Ui.Btn("Copy Setup SQL", () => { Clipboard.SetText(CloudSync.SetupSql); Ui.Message("Copied – paste it in Supabase → SQL Editor and click Run."); })
            .Also(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
    }
}

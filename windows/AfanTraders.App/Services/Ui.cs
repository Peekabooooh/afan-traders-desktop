using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>Small helpers so screens can be built in (compile-checked) C# code.</summary>
static class Ui
{
    public static readonly Brush Navy = Hex("#101828");     // ink: headings and totals
    public static readonly Brush Blue = Hex("#4C5BF0");     // indigo accent
    public static readonly Brush Green = Hex("#0E8F63");
    public static readonly Brush Orange = Hex("#C76A0A");
    public static readonly Brush Red = Hex("#D93A3F");
    public static readonly Brush Muted = Hex("#667085");
    public static readonly Brush Line = Hex("#E7E9F0");
    public static readonly Brush CardBg = Hex("#FFFFFF");
    public static readonly Brush PageBg = Hex("#F4F5F9");
    public static readonly Brush Zebra = Hex("#FAFBFD");
    public static readonly Brush SelectedBg = Hex("#EEF0FE");

    public static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Brush Tint(Brush b, byte alpha) => b is SolidColorBrush s
        ? new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)) : b;

    public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, Brush? color = null, TextWrapping wrap = TextWrapping.NoWrap)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = wrap, VerticalAlignment = VerticalAlignment.Center };
        if (color != null) t.Foreground = color;
        if (wrap == TextWrapping.NoWrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    public static TextBlock Caption(string s) => Text(s, 12, null, Muted);
    public static TextBlock Heading(string s, double size = 15) => Text(s, size, FontWeights.SemiBold);

    public static Image Icon(string name, double size = 22)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        try { img.Source = new BitmapImage(new Uri($"pack://application:,,,/Icons/{name}.png")); } catch { }
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    public static Button Btn(string text, Action onClick, bool primary = false, string? icon = null, Brush? tint = null, string? tooltip = null, Look? look = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) content.Children.Add(Ui.Icon(icon, 18).Also(i => i.Margin = new Thickness(0, 0, text.Length > 0 ? 7 : 0, 0)));
        if (text.Length > 0) content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var kind = look ?? (primary ? Look.Primary : Look.Secondary);
        var b = new Button { Content = content, Style = Theme.Button(kind), Margin = new Thickness(6, 0, 0, 0) };
        if (kind == Look.Primary && tint != null) b.Background = Theme.GradientOf(tint);
        else if (tint != null) b.Foreground = tint;
        if (tooltip != null) b.ToolTip = tooltip;
        b.Click += (_, _) => Safe(onClick);
        return b;
    }

    /// <summary>Small square icon button (print, remove…).</summary>
    public static Button IconBtn(string icon, Action onClick, string tooltip, Look look = Look.Secondary)
    {
        var b = Btn("", onClick, icon: icon, tooltip: tooltip, look: look);
        b.Padding = new Thickness(6, 3, 6, 3); b.MinHeight = 30; b.MinWidth = 32; b.Margin = new Thickness(0);
        return b;
    }

    /// <summary>A glyph from the Windows icon font (Segoe Fluent Icons / MDL2).</summary>
    public static TextBlock Glyph(string code, double size = 14, Brush? color = null) =>
        new() { Text = code, FontFamily = Theme.IconFont, FontSize = size, Foreground = color ?? Muted, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Runs an action and shows a friendly message instead of crashing.</summary>
    public static void Safe(Action a)
    {
        try { a(); }
        catch (AppError e) { Message(e.Message, "Please check"); }
        catch (Exception e) { Message(e.Message, "Something went wrong"); }
    }

    public static void Message(string text, string title = "AFAN Traders") =>
        MessageBox.Show(Application.Current?.MainWindow, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string text, string title = "Please confirm") =>
        MessageBox.Show(Application.Current?.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static StackPanel Row(params UIElement[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    public static StackPanel Col(double gap, params UIElement[] kids)
    {
        var p = new StackPanel();
        for (int i = 0; i < kids.Length; i++)
        {
            if (i > 0 && kids[i] is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(kids[i]);
        }
        return p;
    }

    /// <summary>White squircle card with a soft shadow.</summary>
    public static Squircle Card(UIElement child, double pad = 18) => new()
    {
        Child = child, Padding = new Thickness(pad), Background = CardBg, BorderBrush = Line, BorderThickness = 1,
        CornerRadius = 18, Shadow = 18, ShadowDepth = 3, ShadowOpacity = 0.06,
    };

    /// <summary>Card that holds a table: content cut to the rounded corners, shown and hidden with the table.</summary>
    public static Squircle TableCard(DataGrid grid)
    {
        var c = Card(grid, 0);
        c.ClipContent = true;
        c.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = grid });
        return c;
    }

    /// <summary>A 3D icon on a soft tinted squircle tile.</summary>
    public static Squircle IconTile(string icon, Brush color, double size = 48, double iconSize = 30) => new()
    {
        Width = size, Height = size, CornerRadius = size * 0.3, Background = Tint(color, 24),
        Child = Icon(icon, iconSize).Also(i => { i.HorizontalAlignment = HorizontalAlignment.Center; i.VerticalAlignment = VerticalAlignment.Center; }),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Coloured note strip (warnings, hints).</summary>
    public static Squircle Note(UIElement child, Brush color) => new()
    {
        Child = child, Background = Tint(color, 22), BorderBrush = Tint(color, 60), BorderThickness = 1, CornerRadius = 12, Padding = new Thickness(14, 9, 14, 9),
    };

    /// <summary>Label above a control.</summary>
    public static StackPanel Field(string label, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Theme.Ink2, Margin = new Thickness(1, 0, 0, 5) });
        p.Children.Add(control);
        return p;
    }

    public static TextBox Input(string text, Action<string> changed, bool multiline = false, double? width = null)
    {
        var t = new TextBox { Text = text, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (multiline) { t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 56; t.VerticalContentAlignment = VerticalAlignment.Top; t.VerticalAlignment = VerticalAlignment.Stretch; }
        if (width is { } w) { t.Width = w; t.HorizontalAlignment = HorizontalAlignment.Left; }
        t.TextChanged += (_, _) => changed(t.Text);
        return t;
    }

    /// <summary>Number box: updates the value as you type (commas allowed).</summary>
    public static TextBox Number(double value, Action<double> changed, double width = 90)
    {
        var t = new TextBox { Text = Fmt.Editable(value), Width = width, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.TextChanged += (_, _) => { if (Fmt.Parse(t.Text) is { } v) changed(v); else if (t.Text.T().Length == 0) changed(0); };
        t.LostFocus += (_, _) => t.Text = Fmt.Editable(Fmt.Parse(t.Text) ?? 0);
        t.GotKeyboardFocus += (_, _) => t.SelectAll();
        return t;
    }

    public static DatePicker Date(DateTime value, Action<DateTime> changed)
    {
        var d = new DatePicker { SelectedDate = value, MinHeight = 34, Width = 150, VerticalContentAlignment = VerticalAlignment.Center };
        d.SelectedDateChanged += (_, _) => { if (d.SelectedDate is { } v) changed(v.Date + value.TimeOfDay); };
        return d;
    }

    public static Border Pill(string text, Brush color)
    {
        return new Border
        {
            Background = Tint(color, 26), CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = color },
        };
    }

    public static Image Thumb(byte[]? data, double size = 28)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        img.Source = Images.Load(data) ?? new BitmapImage(new Uri("pack://application:,,,/Icons/inventory.png"));
        img.Clip = Squircle.Shape(new Rect(0, 0, size, size), size * 0.26);
        return img;
    }

    /// <summary>Header bar used by every editor window.</summary>
    public static Border Header(string title, string subtitle, string icon)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, 21, FontWeights.SemiBold, Navy));
        if (subtitle.Length > 0) text.Children.Add(Text(subtitle, 13, null, Muted).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        var row = Row(IconTile(icon, Blue, 52, 34).Also(i => i.Margin = new Thickness(0, 0, 14, 0)), text);
        return new Border { Child = row, Padding = new Thickness(24, 16, 24, 16), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = CardBg };
    }

    /// <summary>Bottom button bar for editor windows: left items, then right items.</summary>
    public static Border Footer(IEnumerable<UIElement> left, IEnumerable<UIElement> right)
    {
        var g = new DockPanel { LastChildFill = false };
        foreach (var l in left) { DockPanel.SetDock(l, Dock.Left); g.Children.Add(l); }
        foreach (var r in right.Reverse()) { DockPanel.SetDock(r, Dock.Right); g.Children.Add(r); }
        return new Border { Child = g, Padding = new Thickness(20, 12, 20, 12), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = CardBg };
    }

    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }

    public static UIElement Spacer(double h = 12) => new Border { Height = h };

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static void OnEnter(this TextBox t, Action a) => t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { a(); e.Handled = true; } };
}

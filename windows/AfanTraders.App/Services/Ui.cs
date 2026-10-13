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
    public static readonly Brush Navy = Hex("#1F3B66");
    public static readonly Brush Blue = Hex("#3373D9");
    public static readonly Brush Green = Hex("#219A5C");
    public static readonly Brush Orange = Hex("#EB8C1A");
    public static readonly Brush Red = Hex("#D94040");
    public static readonly Brush Muted = Hex("#6B7380");
    public static readonly Brush Line = Hex("#E3E7EC");
    public static readonly Brush CardBg = Hex("#FFFFFF");
    public static readonly Brush PageBg = Hex("#F4F6F9");
    public static readonly Brush Zebra = Hex("#F8F9FB");
    public static readonly Brush SelectedBg = Hex("#DCE8FA");

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

    public static Button Btn(string text, Action onClick, bool primary = false, string? icon = null, Brush? tint = null, string? tooltip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) content.Children.Add(Ui.Icon(icon, 18).Also(i => i.Margin = new Thickness(0, 0, 6, 0)));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4, 0, 0, 0), MinHeight = 32 };
        if (primary)
        {
            b.Background = tint ?? Blue;
            b.Foreground = Brushes.White;
            b.BorderBrush = tint ?? Blue;
            b.FontWeight = FontWeights.SemiBold;
        }
        else if (tint != null) b.Foreground = tint;
        if (tooltip != null) b.ToolTip = tooltip;
        b.Click += (_, _) => Safe(onClick);
        return b;
    }

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

    public static Border Card(UIElement child, double pad = 16) => new()
    {
        Child = child, Padding = new Thickness(pad), Background = CardBg, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
    };

    /// <summary>Label above a control.</summary>
    public static StackPanel Field(string label, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 0, 0, 3) });
        p.Children.Add(control);
        return p;
    }

    public static TextBox Input(string text, Action<string> changed, bool multiline = false, double? width = null)
    {
        var t = new TextBox { Text = text, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        if (multiline) { t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 56; t.VerticalContentAlignment = VerticalAlignment.Top; }
        if (width is { } w) t.Width = w;
        t.TextChanged += (_, _) => changed(t.Text);
        return t;
    }

    /// <summary>Number box: updates the value as you type (commas allowed).</summary>
    public static TextBox Number(double value, Action<double> changed, double width = 90)
    {
        var t = new TextBox { Text = Fmt.Editable(value), Width = width, MinHeight = 30, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
        t.TextChanged += (_, _) => { if (Fmt.Parse(t.Text) is { } v) changed(v); else if (t.Text.T().Length == 0) changed(0); };
        t.LostFocus += (_, _) => t.Text = Fmt.Editable(Fmt.Parse(t.Text) ?? 0);
        t.GotKeyboardFocus += (_, _) => t.SelectAll();
        return t;
    }

    public static DatePicker Date(DateTime value, Action<DateTime> changed)
    {
        var d = new DatePicker { SelectedDate = value, MinHeight = 30, Width = 150 };
        d.SelectedDateChanged += (_, _) => { if (d.SelectedDate is { } v) changed(v.Date + value.TimeOfDay); };
        return d;
    }

    public static Border Pill(string text, Brush color)
    {
        return new Border
        {
            Background = Tint(color, 36), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = color },
        };
    }

    public static Image Thumb(byte[]? data, double size = 28)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        img.Source = Images.Load(data) ?? new BitmapImage(new Uri("pack://application:,,,/Icons/inventory.png"));
        img.Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.2, size * 0.2);
        return img;
    }

    /// <summary>Header bar used by every editor window.</summary>
    public static Border Header(string title, string subtitle, string icon)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, 20, FontWeights.SemiBold));
        if (subtitle.Length > 0) text.Children.Add(Text(subtitle, 13, null, Muted));
        var row = Row(Icon(icon, 40).Also(i => i.Margin = new Thickness(0, 0, 12, 0)), text);
        return new Border { Child = row, Padding = new Thickness(20, 14, 20, 14), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = CardBg };
    }

    /// <summary>Bottom button bar for editor windows: left items, then right items.</summary>
    public static Border Footer(IEnumerable<UIElement> left, IEnumerable<UIElement> right)
    {
        var g = new DockPanel { LastChildFill = false };
        foreach (var l in left) { DockPanel.SetDock(l, Dock.Left); g.Children.Add(l); }
        foreach (var r in right.Reverse()) { DockPanel.SetDock(r, Dock.Right); g.Children.Add(r); }
        return new Border { Child = g, Padding = new Thickness(16, 10, 16, 10), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = CardBg };
    }

    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }

    public static UIElement Spacer(double h = 12) => new Border { Height = h };

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static void OnEnter(this TextBox t, Action a) => t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { a(); e.Handled = true; } };
}

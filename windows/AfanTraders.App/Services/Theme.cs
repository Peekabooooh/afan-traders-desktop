using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AfanTraders.App;

/// <summary>Rounded rectangle with continuous ("squircle") corners, like Apple's icons and cards.
/// It draws its own surface, border and soft shadow underneath the content, so text stays crisp.</summary>
class Squircle : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = Reg(nameof(Background), typeof(Brush), null);
    public static readonly DependencyProperty BorderBrushProperty = Reg(nameof(BorderBrush), typeof(Brush), null);
    public static readonly DependencyProperty BorderThicknessProperty = Reg(nameof(BorderThickness), typeof(double), 0.0, measure: true);
    public static readonly DependencyProperty CornerRadiusProperty = Reg(nameof(CornerRadius), typeof(double), 12.0);
    public static readonly DependencyProperty PaddingProperty = Reg(nameof(Padding), typeof(Thickness), new Thickness(), measure: true);
    public static readonly DependencyProperty ClipContentProperty = Reg(nameof(ClipContent), typeof(bool), false);
    public static readonly DependencyProperty ShadowProperty = Reg(nameof(Shadow), typeof(double), 0.0);
    public static readonly DependencyProperty ShadowDepthProperty = Reg(nameof(ShadowDepth), typeof(double), 2.0);
    public static readonly DependencyProperty ShadowOpacityProperty = Reg(nameof(ShadowOpacity), typeof(double), 0.08);
    public static readonly DependencyProperty ShadowColorProperty = Reg(nameof(ShadowColor), typeof(Color), Theme.InkColor);

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    /// <summary>Cut the content to the rounded shape (tables, pictures).</summary>
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }
    /// <summary>Shadow blur in pixels (0 = no shadow).</summary>
    public double Shadow { get => (double)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
    public double ShadowDepth { get => (double)GetValue(ShadowDepthProperty); set => SetValue(ShadowDepthProperty, value); }
    public double ShadowOpacity { get => (double)GetValue(ShadowOpacityProperty); set => SetValue(ShadowOpacityProperty, value); }
    public Color ShadowColor { get => (Color)GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }

    static DependencyProperty Reg(string name, Type type, object? def, bool measure = false) =>
        DependencyProperty.Register(name, type, typeof(Squircle), new FrameworkPropertyMetadata(def,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure : FrameworkPropertyMetadataOptions.AffectsArrange,
            (d, _) => ((Squircle)d).Sync()));

    readonly Path surface = new();
    DropShadowEffect? shadow;

    public Squircle()
    {
        AddVisualChild(surface);
        Sync();
    }

    protected override int VisualChildrenCount => Child == null ? 1 : 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? surface : Child!;

    void Sync()
    {
        surface.Fill = Background;
        surface.Stroke = BorderThickness > 0 ? BorderBrush : null;
        surface.StrokeThickness = BorderThickness;
        if (Shadow > 0 && ShadowOpacity > 0)
        {
            shadow ??= new DropShadowEffect { Direction = 270, RenderingBias = RenderingBias.Quality };
            shadow.BlurRadius = Shadow; shadow.ShadowDepth = ShadowDepth; shadow.Opacity = ShadowOpacity; shadow.Color = ShadowColor;
            surface.Effect = shadow;
        }
        else surface.Effect = null;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding; var b = BorderThickness;
        double hx = p.Left + p.Right + 2 * b, hy = p.Top + p.Bottom + 2 * b;
        if (Child is not { } c) return new Size(hx, hy);
        c.Measure(new Size(Math.Max(0, constraint.Width - hx), Math.Max(0, constraint.Height - hy)));
        return new Size(c.DesiredSize.Width + hx, c.DesiredSize.Height + hy);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var b = BorderThickness; var p = Padding;
        surface.Data = Shape(new Rect(b / 2, b / 2, Math.Max(0, size.Width - b), Math.Max(0, size.Height - b)), Math.Max(0, CornerRadius - b / 2));
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        if (Child is { } c)
        {
            var inner = new Rect(b + p.Left, b + p.Top, Math.Max(0, size.Width - 2 * b - p.Left - p.Right), Math.Max(0, size.Height - 2 * b - p.Top - p.Bottom));
            c.Arrange(inner);
            if (ClipContent)
                c.Clip = Shape(new Rect(b - inner.X, b - inner.Y, Math.Max(0, size.Width - 2 * b), Math.Max(0, size.Height - 2 * b)), Math.Max(0, CornerRadius - b));
        }
        return size;
    }

    /// <summary>Continuous-curvature corners (the curve Apple uses), from three Béziers per corner.</summary>
    public static Geometry Shape(Rect r, double radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return Geometry.Empty;
        const double K = 1.52866483;
        double rad = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2 / K));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            // Each corner: c = corner point, a = direction of travel into it, b = direction out of it.
            var corners = new[]
            {
                (C: r.TopRight, A: new Vector(1, 0), B: new Vector(0, 1)),
                (C: r.BottomRight, A: new Vector(0, 1), B: new Vector(-1, 0)),
                (C: r.BottomLeft, A: new Vector(-1, 0), B: new Vector(0, -1)),
                (C: r.TopLeft, A: new Vector(0, -1), B: new Vector(1, 0)),
            };
            bool first = true;
            foreach (var (c, a, b) in corners)
            {
                Point P(double u, double v) => c - a * (u * rad) + b * (v * rad);
                if (first) { ctx.BeginFigure(P(K, 0), true, true); first = false; }
                else ctx.LineTo(P(K, 0), true, true);
                ctx.BezierTo(P(1.08849323, 0), P(0.86840689, 0), P(0.63149399, 0.07491100), true, true);
                ctx.BezierTo(P(0.37282392, 0.16905899), P(0.16905899, 0.37282392), P(0.07491100, 0.63149399), true, true);
                ctx.BezierTo(P(0, 0.86840689), P(0, 1.08849323), P(0, K), true, true);
            }
        }
        g.Freeze();
        return g;
    }
}

enum Look { Secondary, Primary, Light, Glass, Ghost }

/// <summary>The app's look: palette, squircle buttons, inputs, chips and tables.</summary>
static class Theme
{
    // Palette – deep ink + indigo, with emerald / amber / rose for status.
    public static readonly Color InkColor = C("#101828");
    public static readonly Color AccentColor = C("#4C5BF0");
    public static readonly Brush Ink = B("#101828");
    public static readonly Brush Ink2 = B("#344054");
    public static readonly Brush Placeholder = B("#98A2B3");
    public static readonly Brush InputBorder = B("#D0D5DD");
    public static readonly Brush InputHover = B("#98A2B3");
    public static readonly Brush AccentSoft = B("#EEF0FE");
    public static readonly Brush HeaderBg = B("#F8F9FC");
    public static readonly Brush RowHover = B("#F7F8FE");
    public static readonly Brush GridLine = B("#EEF0F4");
    public static readonly Brush AccentGradient = Gradient(C("#5D6CF8"), C("#6A4CEF"));

    // Sidebar
    public static readonly Brush SideBg = Vertical(C("#0B1222"), C("#151E38"));
    public static readonly Brush SideText = B("#C5CCDA");
    public static readonly Brush SideMuted = B("#7C87A2");
    public static readonly Brush SideHover = B("#14FFFFFF");
    public static readonly Brush SideGreen = B("#34D399");
    public static readonly Brush SideAmber = B("#FBBF24");
    public static readonly Brush SideRed = B("#F87171");

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Style FocusRing { get; private set; } = null!;
    public static Style SearchBox { get; private set; } = null!;
    public static Style Chip { get; private set; } = null!;
    public static Style HeaderLeft { get; private set; } = null!;
    public static Style HeaderRight { get; private set; } = null!;
    public static Style Cell { get; private set; } = null!;
    public static Style Row { get; private set; } = null!;
    static readonly Dictionary<Look, Style> buttons = new();

    public static Style Button(Look look) => buttons[look];

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Brush B(string hex) => Ui.Hex(hex);

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(a.A, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Brush Gradient(Color from, Color to)
    {
        var g = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    static Brush Vertical(Color top, Color bottom)
    {
        var g = new LinearGradientBrush(top, bottom, 90);
        g.Freeze();
        return g;
    }

    /// <summary>Gradient fill for a primary button of any colour.</summary>
    public static Brush GradientOf(Brush b) => b is SolidColorBrush s
        ? (s.Color == AccentColor ? AccentGradient : Gradient(Mix(s.Color, Colors.White, 0.10), Mix(s.Color, Colors.Black, 0.10)))
        : b;

    /// <summary>Installs the styles. Call once after the Fluent theme is switched on.</summary>
    public static void Apply(Application app)
    {
        var r = app.Resources;
        // Fluent controls we keep (drop-downs, date pickers, check boxes) follow the same accent and roundness.
        foreach (var (key, color) in new[] { ("SystemAccentColor", AccentColor), ("SystemAccentColorLight1", C("#6B78F3")), ("SystemAccentColorLight2", C("#8C96F6")),
                                             ("SystemAccentColorLight3", C("#B3B9FA")), ("SystemAccentColorDark1", C("#3F4CD6")), ("SystemAccentColorDark2", C("#3440B8")),
                                             ("SystemAccentColorDark3", C("#28329A")) })
            r[key] = color;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush", "TextControlFocusedBorderBrush" })
            r[key] = new SolidColorBrush(AccentColor);
        r["AccentFillColorSecondaryBrush"] = new SolidColorBrush(C("#5D6AF2"));
        r["AccentFillColorTertiaryBrush"] = new SolidColorBrush(C("#6E7AF3"));
        r["ControlCornerRadius"] = new CornerRadius(8);
        r["OverlayCornerRadius"] = new CornerRadius(12);

        FocusRing = MakeFocusRing();
        foreach (var look in Enum.GetValues<Look>()) buttons[look] = MakeButton(look);
        Chip = MakeChip();
        r[typeof(TextBox)] = MakeTextBox(false);
        SearchBox = MakeTextBox(true);
        HeaderLeft = MakeHeader(HorizontalAlignment.Left);
        HeaderRight = MakeHeader(HorizontalAlignment.Right);
        Cell = MakeCell();
        Row = MakeRow(app);
    }

    /// <summary>Window defaults: font, layout rounding and a title bar that matches the page (Windows 11).</summary>
    public static void Window(Window w, Brush titleBar)
    {
        w.FontFamily = Font;
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                if (titleBar is not SolidColorBrush s) return;
                var hwnd = new WindowInteropHelper(w).Handle;
                int caption = s.Color.R | s.Color.G << 8 | s.Color.B << 16;
                int text = 0x281810;   // ink
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);      // DWMWA_TEXT_COLOR
            }
            catch { }
        };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // MARK: Builders

    static FrameworkElementFactory F(Type t, string? name = null) => name == null ? new FrameworkElementFactory(t) : new FrameworkElementFactory(t, name);

    static Trigger When(DependencyProperty p, object value, params (string? Target, DependencyProperty Prop, object Value)[] sets)
    {
        var t = new Trigger { Property = p, Value = value };
        foreach (var (target, prop, v) in sets) t.Setters.Add(target == null ? new Setter(prop, v) : new Setter(prop, v, target));
        return t;
    }

    static Setter Set(DependencyProperty p, object? v) => new(p, v);

    static Style MakeFocusRing()
    {
        var ring = F(typeof(Squircle));
        ring.SetValue(Squircle.BorderBrushProperty, new SolidColorBrush(AccentColor));
        ring.SetValue(Squircle.BorderThicknessProperty, 2.0);
        ring.SetValue(Squircle.CornerRadiusProperty, 12.0);
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var s = new Style(typeof(Control));
        s.Setters.Add(Set(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = ring }));
        return s;
    }

    /// <summary>Squircle surface + hover overlay + content: shared by buttons and chips.</summary>
    static ControlTemplate ButtonTemplate(Type type, double radius, Brush hover, Brush pressed, bool raised)
    {
        var root = F(typeof(Grid));
        var bd = F(typeof(Squircle), "bd");
        bd.SetValue(Squircle.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Squircle.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Squircle.BorderThicknessProperty, 1.0);
        bd.SetValue(Squircle.CornerRadiusProperty, radius);
        if (raised)
        {
            bd.SetValue(Squircle.ShadowProperty, 10.0);
            bd.SetValue(Squircle.ShadowDepthProperty, 2.0);
            bd.SetValue(Squircle.ShadowOpacityProperty, 0.16);
        }
        var overlay = F(typeof(Squircle), "hover");
        overlay.SetValue(Squircle.BackgroundProperty, hover);
        overlay.SetValue(Squircle.CornerRadiusProperty, radius);
        overlay.SetValue(UIElement.OpacityProperty, 0.0);
        overlay.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var cp = F(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        cp.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        root.AppendChild(bd);
        root.AppendChild(overlay);
        root.AppendChild(cp);
        var t = new ControlTemplate(type) { VisualTree = root };
        t.Triggers.Add(When(UIElement.IsMouseOverProperty, true, ("hover", UIElement.OpacityProperty, 1.0)));
        t.Triggers.Add(When(ButtonBase.IsPressedProperty, true, ("hover", UIElement.OpacityProperty, 1.0), ("hover", Squircle.BackgroundProperty, pressed)));
        t.Triggers.Add(When(UIElement.IsEnabledProperty, false, (null, UIElement.OpacityProperty, 0.45)));
        return t;
    }

    static Style Common(Type type)
    {
        var s = new Style(type);
        s.Setters.Add(Set(FrameworkElement.OverridesDefaultStyleProperty, true));
        s.Setters.Add(Set(FrameworkElement.FocusVisualStyleProperty, FocusRing));
        s.Setters.Add(Set(Control.PaddingProperty, new Thickness(14, 7, 14, 7)));
        s.Setters.Add(Set(FrameworkElement.MinHeightProperty, 34.0));
        s.Setters.Add(Set(Control.FontSizeProperty, 13.0));
        s.Setters.Add(Set(Control.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(Set(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        s.Setters.Add(Set(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(Set(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        s.Setters.Add(Set(UIElement.SnapsToDevicePixelsProperty, true));
        return s;
    }

    static Style MakeButton(Look look)
    {
        var s = Common(typeof(Button));
        Brush bg, border, fg, hover, pressed;
        bool raised = false;
        switch (look)
        {
            case Look.Primary:
                bg = AccentGradient; border = Brushes.Transparent; fg = Brushes.White; raised = true;
                hover = B("#26FFFFFF"); pressed = B("#22000000"); break;
            case Look.Light:
                bg = Brushes.White; border = Brushes.Transparent; fg = new SolidColorBrush(AccentColor); raised = true;
                hover = B("#0F4C5BF0"); pressed = B("#1F4C5BF0"); break;
            case Look.Glass:
                bg = B("#1FFFFFFF"); border = B("#40FFFFFF"); fg = Brushes.White;
                hover = B("#1FFFFFFF"); pressed = B("#33FFFFFF"); break;
            case Look.Ghost:
                bg = Brushes.Transparent; border = Brushes.Transparent; fg = Ink2;
                hover = B("#0F101828"); pressed = B("#1F101828"); break;
            default:
                bg = Brushes.White; border = B("#D5D9E2"); fg = B("#1D2939");
                hover = B("#0A101828"); pressed = B("#17101828"); break;
        }
        s.Setters.Add(Set(Control.BackgroundProperty, bg));
        s.Setters.Add(Set(Control.BorderBrushProperty, border));
        s.Setters.Add(Set(Control.ForegroundProperty, fg));
        s.Setters.Add(Set(Control.TemplateProperty, ButtonTemplate(typeof(Button), 10, hover, pressed, raised)));
        return s;
    }

    /// <summary>Filter and report tabs: white chips; the chosen one is filled with the accent.</summary>
    static Style MakeChip()
    {
        var s = Common(typeof(ToggleButton));
        s.Setters.Add(Set(Control.BackgroundProperty, Brushes.White));
        s.Setters.Add(Set(Control.BorderBrushProperty, B("#E1E4EB")));
        s.Setters.Add(Set(Control.ForegroundProperty, Ink2));
        s.Setters.Add(Set(Control.FontWeightProperty, FontWeights.Medium));
        s.Setters.Add(Set(FrameworkElement.MinHeightProperty, 32.0));
        var t = ButtonTemplate(typeof(ToggleButton), 10, B("#0A101828"), B("#17101828"), false);
        t.Triggers.Insert(0, When(ToggleButton.IsCheckedProperty, true,
            (null, Control.BackgroundProperty, AccentGradient), (null, Control.BorderBrushProperty, Brushes.Transparent),
            (null, Control.ForegroundProperty, Brushes.White), ("bd", Squircle.ShadowProperty, 10.0), ("bd", Squircle.ShadowOpacityProperty, 0.18)));
        s.Setters.Add(Set(Control.TemplateProperty, t));
        return s;
    }

    /// <summary>Text boxes: white squircle, accent border and glow while typing. The search style adds a magnifier and hint.</summary>
    static Style MakeTextBox(bool search)
    {
        var s = new Style(typeof(TextBox));
        s.Setters.Add(Set(FrameworkElement.OverridesDefaultStyleProperty, true));
        s.Setters.Add(Set(FrameworkElement.FocusVisualStyleProperty, null));
        s.Setters.Add(Set(Control.BackgroundProperty, Brushes.White));
        s.Setters.Add(Set(Control.BorderBrushProperty, InputBorder));
        s.Setters.Add(Set(Control.ForegroundProperty, Ink));
        s.Setters.Add(Set(Control.FontSizeProperty, 13.5));
        s.Setters.Add(Set(Control.PaddingProperty, search ? new Thickness(34, 6, 10, 6) : new Thickness(10, 6, 10, 6)));
        s.Setters.Add(Set(FrameworkElement.MinHeightProperty, 34.0));
        s.Setters.Add(Set(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(Set(TextBoxBase.SelectionBrushProperty, new SolidColorBrush(AccentColor)));
        s.Setters.Add(Set(TextBoxBase.CaretBrushProperty, Ink));
        s.Setters.Add(Set(UIElement.SnapsToDevicePixelsProperty, true));
        s.Setters.Add(Set(Validation.ErrorTemplateProperty, null));

        var root = F(typeof(Grid));
        var bd = F(typeof(Squircle), "bd");
        bd.SetValue(Squircle.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Squircle.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Squircle.BorderThicknessProperty, 1.0);
        bd.SetValue(Squircle.CornerRadiusProperty, 10.0);
        bd.SetValue(Squircle.ShadowProperty, 4.0);
        bd.SetValue(Squircle.ShadowDepthProperty, 1.0);
        bd.SetValue(Squircle.ShadowOpacityProperty, 0.05);
        root.AppendChild(bd);
        if (search)
        {
            var glass = F(typeof(TextBlock));
            glass.SetValue(TextBlock.TextProperty, "");
            glass.SetValue(TextBlock.FontFamilyProperty, IconFont);
            glass.SetValue(TextBlock.FontSizeProperty, 13.0);
            glass.SetValue(TextBlock.ForegroundProperty, Placeholder);
            glass.SetValue(FrameworkElement.MarginProperty, new Thickness(13, 0, 0, 0));
            glass.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            glass.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glass.SetValue(UIElement.IsHitTestVisibleProperty, false);
            root.AppendChild(glass);
        }
        // The text box applies its Padding to the text itself, so only the hint needs it here.
        var hint = F(typeof(TextBlock), "hint");
        hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(FrameworkElement.Tag)) { RelativeSource = RelativeSource.TemplatedParent });
        hint.SetValue(TextBlock.ForegroundProperty, Placeholder);
        hint.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        hint.SetValue(TextBlock.PaddingProperty, new Thickness(2, 0, 0, 0));
        hint.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        hint.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        hint.SetValue(UIElement.IsHitTestVisibleProperty, false);
        hint.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var host = F(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        host.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        root.AppendChild(hint);
        root.AppendChild(host);

        var t = new ControlTemplate(typeof(TextBox)) { VisualTree = root };
        t.Triggers.Add(When(TextBox.TextProperty, "", ("hint", UIElement.VisibilityProperty, Visibility.Visible)));
        t.Triggers.Add(When(TextBoxBase.IsReadOnlyProperty, true, ("bd", Squircle.BackgroundProperty, B("#F9FAFB"))));
        t.Triggers.Add(When(UIElement.IsMouseOverProperty, true, ("bd", Squircle.BorderBrushProperty, InputHover)));
        t.Triggers.Add(When(UIElement.IsKeyboardFocusWithinProperty, true,
            ("bd", Squircle.BorderBrushProperty, new SolidColorBrush(AccentColor)), ("bd", Squircle.ShadowColorProperty, AccentColor),
            ("bd", Squircle.ShadowProperty, 9.0), ("bd", Squircle.ShadowDepthProperty, 0.0), ("bd", Squircle.ShadowOpacityProperty, 0.45)));
        t.Triggers.Add(When(UIElement.IsEnabledProperty, false, ("bd", Squircle.BackgroundProperty, B("#F2F4F7")), (null, Control.ForegroundProperty, Ui.Muted)));
        s.Setters.Add(Set(Control.TemplateProperty, t));
        return s;
    }

    /// <summary>Table headers: quiet grey band, small semibold labels, sort arrow, resize grip.</summary>
    static Style MakeHeader(HorizontalAlignment align)
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(Set(FrameworkElement.OverridesDefaultStyleProperty, true));
        s.Setters.Add(Set(Control.BackgroundProperty, HeaderBg));
        s.Setters.Add(Set(Control.ForegroundProperty, Ui.Muted));
        s.Setters.Add(Set(Control.FontSizeProperty, 12.0));
        s.Setters.Add(Set(Control.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(Set(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        s.Setters.Add(Set(Control.HorizontalContentAlignmentProperty, align));
        s.Setters.Add(Set(FrameworkElement.MinHeightProperty, 42.0));
        s.Setters.Add(Set(UIElement.SnapsToDevicePixelsProperty, true));

        var root = F(typeof(Grid));
        var bd = F(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, Ui.Line);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        root.AppendChild(bd);
        var row = F(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        row.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var cp = F(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var arrow = F(typeof(TextBlock), "arrow");
        arrow.SetValue(TextBlock.FontFamilyProperty, IconFont);
        arrow.SetValue(TextBlock.FontSizeProperty, 9.0);
        arrow.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(AccentColor));
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 1, 0, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        row.AppendChild(cp);
        row.AppendChild(arrow);
        root.AppendChild(row);
        var grip = F(typeof(Thumb), "PART_RightHeaderGripper");
        grip.SetValue(FrameworkElement.WidthProperty, 8.0);
        grip.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        grip.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.SizeWE);
        var gripLook = F(typeof(Border));
        gripLook.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        grip.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(Thumb)) { VisualTree = gripLook });
        root.AppendChild(grip);

        var t = new ControlTemplate(typeof(DataGridColumnHeader)) { VisualTree = root };
        t.Triggers.Add(When(DataGridColumnHeader.SortDirectionProperty, ListSortDirection.Ascending, ("arrow", TextBlock.TextProperty, ""), ("arrow", UIElement.VisibilityProperty, Visibility.Visible)));
        t.Triggers.Add(When(DataGridColumnHeader.SortDirectionProperty, ListSortDirection.Descending, ("arrow", TextBlock.TextProperty, ""), ("arrow", UIElement.VisibilityProperty, Visibility.Visible)));
        t.Triggers.Add(When(UIElement.IsMouseOverProperty, true, (null, Control.ForegroundProperty, Ink2)));
        s.Setters.Add(Set(Control.TemplateProperty, t));
        return s;
    }

    static Style MakeCell()
    {
        var s = new Style(typeof(DataGridCell));
        s.Setters.Add(Set(FrameworkElement.FocusVisualStyleProperty, null));
        s.Setters.Add(Set(Control.BackgroundProperty, Brushes.Transparent));
        s.Setters.Add(Set(Control.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(Set(Control.ForegroundProperty, Ink));
        var bd = F(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var cp = F(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        bd.AppendChild(cp);
        s.Setters.Add(Set(Control.TemplateProperty, new ControlTemplate(typeof(DataGridCell)) { VisualTree = bd }));
        return s;
    }

    static Style MakeRow(Application app)
    {
        var s = new Style(typeof(DataGridRow), app.TryFindResource(typeof(DataGridRow)) as Style);
        s.Setters.Add(Set(Control.BackgroundProperty, Brushes.White));
        s.Setters.Add(Set(Control.ForegroundProperty, Ink));
        s.Triggers.Add(When(UIElement.IsMouseOverProperty, true, (null, Control.BackgroundProperty, RowHover)));
        s.Triggers.Add(When(DataGridRow.IsSelectedProperty, true, (null, Control.BackgroundProperty, AccentSoft)));
        return s;
    }
}

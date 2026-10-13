using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace AfanTraders.Core;

/// <summary>Everything needed to print a quotation, delivery challan or invoice.</summary>
public sealed record PrintableDoc
{
    public enum DocKind { Quotation, Challan, Invoice }
    public DocKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Number { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public string PoNumber { get; init; } = "";
    public IReadOnlyList<(string Key, string Value)> Info { get; init; } = Array.Empty<(string, string)>();
    public string CustomerName { get; init; } = "";
    public string CustomerAddress { get; init; } = "";
    public string CustomerPhone { get; init; } = "";
    public IReadOnlyList<DocLine> Lines { get; init; } = Array.Empty<DocLine>();
    public double TaxRate { get; init; }
    public string Notes { get; init; } = "";
    public string Terms { get; init; } = "";

    public bool ShowPrices => Kind != DocKind.Challan;
    public double Subtotal => Fmt.R2(Lines.Sum(l => l.Total));
    public double TaxAmount => Fmt.R2(Subtotal * TaxRate / 100);
    public double GrandTotal => Fmt.R2(Subtotal + TaxAmount);
    public double TotalQty => Lines.Sum(l => l.Qty);

    public string FileName
    {
        get
        {
            var safe = string.Join("-", Number.Split(Path.GetInvalidFileNameChars()));
            return Kind switch { DocKind.Quotation => $"Quotation {safe}.pdf", DocKind.Challan => $"Delivery Challan {safe}.pdf", _ => $"Invoice {safe}.pdf" };
        }
    }

    public static PrintableDoc Sample(DocKind kind, CompanySettings s)
    {
        var lines = Enumerable.Range(1, 6).Select(i => new DocLine
        {
            Code = $"ITEM-{100 + i}", Name = $"Sample item {i}", Details = i % 2 == 0 ? "Size / brand / model" : "", Unit = "Pcs",
            Qty = i * 2, Rate = 1250 * i, BaseRate = 1250 * i,
        }).ToList();
        var prefix = kind == DocKind.Quotation ? s.QuotationPrefix : kind == DocKind.Challan ? s.ChallanPrefix : s.InvoicePrefix;
        return new PrintableDoc
        {
            Kind = kind, Title = kind switch { DocKind.Quotation => "QUOTATION", DocKind.Challan => "DELIVERY CHALLAN", _ => s.InvoiceTitle.T().Length == 0 ? "INVOICE" : s.InvoiceTitle.ToUpperInvariant() },
            Number = $"{prefix}-26-00001", PoNumber = "PO-1234", Info = new[] { ("Quotation No.", $"{s.QuotationPrefix}-26-00001") },
            CustomerName = "Sample Customer (Pvt) Ltd", CustomerAddress = "Plot 12, Industrial Area\nLahore", CustomerPhone = "0300-0000000",
            Lines = lines, TaxRate = s.DefaultTaxRate, Notes = "This is a sample to check your letterhead layout.",
            Terms = kind == DocKind.Invoice ? s.InvoiceTerms : s.QuotationTerms,
        };
    }
}

public sealed partial class Store
{
    public PrintableDoc Printable(Quotation q) => new()
    {
        Kind = PrintableDoc.DocKind.Quotation, Title = "QUOTATION", Number = q.Number, Date = q.Date, PoNumber = q.PoNumber,
        CustomerName = q.CustomerName, CustomerAddress = q.CustomerAddress, CustomerPhone = q.CustomerPhone, Lines = q.Lines,
        TaxRate = q.TaxRate, Notes = q.Notes, Terms = Settings.QuotationTerms,
    };

    public PrintableDoc Printable(Challan c)
    {
        var info = new List<(string, string)>();
        if (QuotationById(c.QuotationId) is { } q) info.Add(("Quotation No.", q.Number));
        if (c.VehicleNo.Length > 0) info.Add(("Vehicle No.", c.VehicleNo));
        if (c.DriverName.Length > 0) info.Add(("Driver", c.DriverName));
        return new()
        {
            Kind = PrintableDoc.DocKind.Challan, Title = "DELIVERY CHALLAN", Number = c.Number, Date = c.Date, PoNumber = c.PoNumber, Info = info,
            CustomerName = c.CustomerName, CustomerAddress = c.CustomerAddress, CustomerPhone = c.CustomerPhone, Lines = c.Lines, Notes = c.Notes,
        };
    }

    public PrintableDoc Printable(Invoice inv)
    {
        var info = new List<(string, string)>();
        if (QuotationById(inv.QuotationId) is { } q) info.Add(("Quotation No.", q.Number));
        if (ChallanById(inv.ChallanId) is { } c) info.Add(("Delivery Challan", c.Number));
        return new()
        {
            Kind = PrintableDoc.DocKind.Invoice, Title = Settings.InvoiceTitle.T().Length == 0 ? "INVOICE" : Settings.InvoiceTitle.ToUpperInvariant(),
            Number = inv.Number, Date = inv.Date, PoNumber = inv.PoNumber, Info = info, CustomerName = inv.CustomerName,
            CustomerAddress = inv.CustomerAddress, CustomerPhone = inv.CustomerPhone, Lines = inv.Lines, TaxRate = inv.TaxRate,
            Notes = inv.Notes, Terms = Settings.InvoiceTerms,
        };
    }
}

/// <summary>Noto Sans, bundled inside the app so PDFs look the same on every computer.</summary>
public sealed class BundledFonts : IFontResolver
{
    public const string Family = "Noto Sans";
    static int installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 0) GlobalFontSettings.FontResolver = new BundledFonts();
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(italic ? "NotoSans-Italic" : bold ? "NotoSans-Bold" : "NotoSans-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var s = typeof(BundledFonts).Assembly.GetManifestResourceStream($"Fonts.{faceName}.ttf");
        if (s == null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}

/// <summary>Draws A4 documents – same layout as the Mac app's PDFPageView.</summary>
public static class PdfDocs
{
    public const double PageW = 595.28, PageH = 841.89;
    const double Margin = 40;
    const double HeaderH = 112, FooterH = 40, TitleH = 46, InfoH = 112, TableHeadH = 24, PageNoH = 18;

    static readonly XColor Navy = XColor.FromArgb(31, 59, 102);
    static readonly XColor Ink = XColor.FromArgb(31, 33, 41);
    static readonly XColor Muted = XColor.FromArgb(107, 115, 128);
    static readonly XColor Rule = XColor.FromArgb(209, 217, 227);
    static readonly XColor Zebra = XColor.FromArgb(10, 31, 59, 102);

    static XFont F(double size, bool bold = false, bool italic = false) =>
        new(BundledFonts.Family, size, italic ? XFontStyleEx.Italic : bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    static double RowH(DocLine l) => string.IsNullOrWhiteSpace(l.Details) ? 22 : 34;

    public sealed record Page(List<(int Index, DocLine Line)> Rows, int Number, int Count)
    {
        public bool IsFirst => Number == 1;
        public bool IsLast => Number == Count;
    }

    public static byte[] Render(PrintableDoc doc, CompanySettings s)
    {
        BundledFonts.Install();
        var letterhead = Letterhead.From(s.Letterhead);
        var pages = Paginate(doc, s, letterhead != null);
        using var pdf = new PdfDocument();
        pdf.Info.Title = doc.FileName.Replace(".pdf", "");
        foreach (var p in pages)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(PageW);
            page.Height = XUnit.FromPoint(PageH);
            using var g = XGraphics.FromPdfPage(page);
            letterhead?.Draw(g);
            DrawPage(g, doc, p, s, letterhead != null);
        }
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }

    public static List<Page> Paginate(PrintableDoc doc, CompanySettings s, bool hasLetterhead)
    {
        BundledFonts.Install();
        double top = hasLetterhead ? s.LetterheadTop : HeaderH, bottom = hasLetterhead ? s.LetterheadBottom : FooterH;
        double usable = PageH - top - bottom - PageNoH - TitleH;
        double firstBudget = usable - InfoH - TableHeadH, otherBudget = usable - TableHeadH;
        var pages = new List<List<(int, DocLine)>> { new() };
        double used = 0, budget = firstBudget;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            var h = RowH(doc.Lines[i]);
            if (used + h > budget && pages[^1].Count > 0) { pages.Add(new()); used = 0; budget = otherBudget; }
            pages[^1].Add((i, doc.Lines[i]));
            used += h;
        }
        // Totals, notes and signatures must fit under the last rows.
        using var measure = XGraphics.CreateMeasureContext(new XSize(PageW, PageH), XGraphicsUnit.Point, XPageDirection.Downwards);
        if (used + Closing(measure, doc, s, 0, draw: false) > budget) pages.Add(new());
        return pages.Select((rows, n) => new Page(rows, n + 1, pages.Count)).ToList();
    }

    static void DrawPage(XGraphics g, PrintableDoc doc, Page page, CompanySettings s, bool hasLetterhead)
    {
        double y = hasLetterhead ? s.LetterheadTop : 0;
        if (!hasLetterhead) { BuiltInHeader(g, s); y = HeaderH; }

        // Title bar
        double baseline = y + 33;   // title and number share one baseline
        Tracked(g, doc.Title, F(17, true), Navy, Margin, baseline, 2);
        if (!page.IsFirst) g.DrawString("(continued)", F(9), new XSolidBrush(Muted), Margin + Width(g, doc.Title, F(17, true), 2) + 10, baseline);
        var numW = g.MeasureString(doc.Number, F(13, true)).Width;
        g.DrawString(doc.Number, F(13, true), new XSolidBrush(Ink), PageW - Margin - numW, baseline);
        g.DrawString("No.", F(10), new XSolidBrush(Muted), PageW - Margin - numW - 24, baseline);
        y += TitleH;

        if (page.IsFirst) { InfoBlock(g, doc, y); y += InfoH; }

        if (page.Rows.Count > 0)
        {
            TableHeader(g, doc, y);
            y += TableHeadH;
            foreach (var (index, line) in page.Rows)
            {
                var h = RowH(line);
                TableRow(g, doc, index, line, y, h);
                y += h;
            }
        }
        if (page.IsLast) Closing(g, doc, s, y, draw: true);

        // Page number line and footer
        double bottomEdge = PageH - (hasLetterhead ? s.LetterheadBottom : FooterH);
        var pn = $"Page {page.Number} of {page.Count}";
        g.DrawString($"{doc.Title} · {doc.Number}", F(7.5), new XSolidBrush(Muted), Margin, bottomEdge - 6);
        g.DrawString(pn, F(7.5), new XSolidBrush(Muted), PageW - Margin - g.MeasureString(pn, F(7.5)).Width, bottomEdge - 6);
        if (!hasLetterhead)
        {
            g.DrawLine(new XPen(Rule, 0.6), Margin, bottomEdge, PageW - Margin, bottomEdge);
            g.DrawString("Thank you for your business!", F(8, true), new XSolidBrush(Navy), Margin, bottomEdge + 14);
            g.DrawString(s.CompanyName, F(8), new XSolidBrush(Muted), PageW - Margin - g.MeasureString(s.CompanyName, F(8)).Width, bottomEdge + 14);
        }
    }

    static void BuiltInHeader(XGraphics g, CompanySettings s)
    {
        double x = Margin;
        if (s.Logo is { Length: > 0 } logo)
        {
            try
            {
                using var img = XImage.FromStream(new MemoryStream(logo));
                double scale = Math.Min(62 / img.PointWidth, 62 / img.PointHeight);
                g.DrawImage(img, x, 94 - 62 + (62 - img.PointHeight * scale) / 2, img.PointWidth * scale, img.PointHeight * scale);
                x += 62 + 14;
            }
            catch { /* unreadable logo – skip it */ }
        }
        Tracked(g, s.CompanyName, F(22, true), Navy, x, 76, 1);
        if (s.Tagline.Length > 0) g.DrawString(s.Tagline, F(9.5), new XSolidBrush(Muted), x, 91);

        var lines = s.Address.Split('\n').Select(l => l.T()).Where(l => l.Length > 0).ToList();
        var contact = new[] { s.Phone.Length > 0 ? $"Tel: {s.Phone}" : null, s.Email.Length > 0 ? s.Email : null }.Where(v => v != null).ToList();
        if (contact.Count > 0) lines.Add(string.Join("  ·  ", contact));
        var tax = new[] { s.Ntn.Length > 0 ? $"NTN: {s.Ntn}" : null, s.Strn.Length > 0 ? $"STRN: {s.Strn}" : null }.Where(v => v != null).ToList();
        if (tax.Count > 0) lines.Add(string.Join("  ·  ", tax));
        lines = lines.Take(5).ToList();
        double ly = 94 - lines.Count * 11 + 8;
        foreach (var l in lines)
        {
            g.DrawString(l, F(8.5), new XSolidBrush(Muted), PageW - Margin - g.MeasureString(l, F(8.5)).Width, ly);
            ly += 11;
        }
        g.DrawRectangle(new XSolidBrush(Navy), Margin, 106, PageW - Margin * 2, 2.5);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(64, 31, 59, 102)), Margin, 110.5, PageW - Margin * 2, 1);
    }

    static void InfoBlock(XGraphics g, PrintableDoc doc, double y)
    {
        double h = InfoH - 12, rightW = 215, gap = 12;
        double leftW = PageW - Margin * 2 - rightW - gap;
        Box(g, Margin, y, leftW, h, doc.Kind switch { PrintableDoc.DocKind.Challan => "DELIVER TO", PrintableDoc.DocKind.Quotation => "QUOTATION FOR", _ => "BILL TO" });
        double cy = y + 30;
        g.DrawString(Ellipsize(g, doc.CustomerName, F(11, true), leftW - 20), F(11, true), new XSolidBrush(Ink), Margin + 10, cy + 3);
        cy += 16;
        foreach (var l in Wrap(g, doc.CustomerAddress, F(9), leftW - 20, 3)) { g.DrawString(l, F(9), new XSolidBrush(Muted), Margin + 10, cy); cy += 12; }
        if (doc.CustomerPhone.Length > 0) g.DrawString($"Tel: {doc.CustomerPhone}", F(9), new XSolidBrush(Muted), Margin + 10, cy);

        double rx = Margin + leftW + gap;
        Box(g, rx, y, rightW, h, "DOCUMENT DETAILS");
        var kvs = new List<(string, string)> { ("Date", Fmt.Date(doc.Date)), ("PO No.", doc.PoNumber.Length == 0 ? "—" : doc.PoNumber) };
        kvs.AddRange(doc.Info.Take(4));
        double ky = y + 33;
        foreach (var (k, v) in kvs)
        {
            g.DrawString(k, F(8.5), new XSolidBrush(Muted), rx + 10, ky);
            g.DrawString(Ellipsize(g, v, F(9, true), rightW - 102), F(9, true), new XSolidBrush(Ink), rx + 10 + 88, ky);
            ky += 13;
        }
    }

    static void Box(XGraphics g, double x, double y, double w, double h, string title)
    {
        g.DrawRoundedRectangle(new XPen(Rule, 0.7), new XSolidBrush(Zebra), x, y, w, h, 12, 12);
        Tracked(g, title, F(7.5, true), Navy, x + 10, y + 16, 1.2);
    }

    static (string Label, double W, bool Right)[] Columns(PrintableDoc doc)
    {
        var cols = new List<(string, double, bool)> { ("S#", 22, false), ("ITEM ID", 70, false), ("DESCRIPTION", 0, false), ("UNIT", 34, false), ("QTY", 44, true) };
        if (doc.ShowPrices) { cols.Add(("RATE", 70, true)); cols.Add(("AMOUNT", 82, true)); }
        double fixedW = cols.Sum(c => c.Item2) + 6 * (cols.Count - 1) + 16;
        var desc = PageW - Margin * 2 - fixedW;
        return cols.Select(c => c.Item1 == "DESCRIPTION" ? (c.Item1, desc, c.Item3) : c).ToArray();
    }

    static IEnumerable<(double X, double W, bool Right)> Layout(PrintableDoc doc)
    {
        double x = Margin + 8;
        foreach (var c in Columns(doc)) { yield return (x, c.W, c.Right); x += c.W + 6; }
    }

    static void TableHeader(XGraphics g, PrintableDoc doc, double y)
    {
        g.DrawRoundedRectangle(new XSolidBrush(Navy), Margin, y, PageW - Margin * 2, TableHeadH, 6, 6);
        var cols = Columns(doc);
        int i = 0;
        foreach (var (x, w, right) in Layout(doc))
        {
            var label = cols[i++].Label;
            var tw = Width(g, label, F(7.5, true), 0.6);
            Tracked(g, label, F(7.5, true), XColors.White, right ? x + w - tw : x, y + 15, 0.6);
        }
    }

    static void TableRow(XGraphics g, PrintableDoc doc, int index, DocLine l, double y, double h)
    {
        if (index % 2 == 1) g.DrawRectangle(new XSolidBrush(Zebra), Margin, y, PageW - Margin * 2, h);
        g.DrawLine(new XPen(Rule, 0.5), Margin, y + h, PageW - Margin, y + h);
        var cols = Layout(doc).ToArray();
        double mid = y + h / 2 + 3.2;
        void Cell(int c, string text, XFont font, XColor color, double baseline)
        {
            var (x, w, right) = cols[c];
            text = Ellipsize(g, text, font, w);
            var tx = right ? x + w - g.MeasureString(text, font).Width : x;
            g.DrawString(text, font, new XSolidBrush(color), tx, baseline);
        }
        Cell(0, $"{index + 1}", F(9), Muted, mid);
        Cell(1, l.Code, F(8.5, true), Ink, mid);
        if (string.IsNullOrWhiteSpace(l.Details)) Cell(2, l.Name, F(9), Ink, mid);
        else { Cell(2, l.Name, F(9), Ink, y + 14); Cell(2, l.Details, F(7.5), Muted, y + 25); }
        Cell(3, l.Unit, F(9), Muted, mid);
        Cell(4, Fmt.Qty(l.Qty), F(9), Ink, mid);
        if (doc.ShowPrices)
        {
            Cell(5, Fmt.Money(l.Rate), F(9), Ink, mid);
            Cell(6, Fmt.Money(l.Total), F(9, true), Ink, mid);
        }
    }

    /// <summary>Totals, notes, terms and signatures. Returns the height (also used to measure).</summary>
    static double Closing(XGraphics g, PrintableDoc doc, CompanySettings s, double top, bool draw)
    {
        double y = top + 10;
        double contentW = PageW - Margin * 2;
        if (doc.ShowPrices)
        {
            double leftW = contentW - 220 - 16;
            var words = Wrap(g, Fmt.Words(doc.GrandTotal, s.CurrencyWord), F(9, italic: true), leftW, 3);
            if (draw)
            {
                Tracked(g, "AMOUNT IN WORDS", F(7.5, true), Navy, Margin, y + 8, 1);
                double wy = y + 21;
                foreach (var w in words) { g.DrawString(w, F(9, italic: true), new XSolidBrush(Ink), Margin, wy); wy += 12; }
                double tx = Margin + contentW - 220;
                TotalRow(g, tx, y, "Subtotal", Fmt.Money(doc.Subtotal));
                TotalRow(g, tx, y + 22, $"Sales Tax @ {Fmt.Qty(doc.TaxRate)}%", Fmt.Money(doc.TaxAmount));
                g.DrawRoundedRectangle(new XSolidBrush(Navy), tx, y + 44, 220, 26, 6, 6);
                Tracked(g, "GRAND TOTAL", F(9, true), XColors.White, tx + 10, y + 61, 0.8);
                var gt = $"{s.Currency} {Fmt.Money(doc.GrandTotal)}";
                g.DrawString(gt, F(11, true), XBrushes.White, tx + 210 - g.MeasureString(gt, F(11, true)).Width, y + 61.5);
            }
            y += Math.Max(70, 21 + words.Count * 12);
        }
        else
        {
            if (draw)
            {
                var q = Fmt.Qty(doc.TotalQty);
                var qw = g.MeasureString(q, F(10, true)).Width;
                g.DrawString(q, F(10, true), new XSolidBrush(Ink), Margin + contentW - qw, y + 12);
                g.DrawString("Total Quantity:", F(9), new XSolidBrush(Muted), Margin + contentW - qw - 6 - g.MeasureString("Total Quantity:", F(9)).Width, y + 12);
            }
            y += 16;
        }
        void Block(string title, string text, int maxLines)
        {
            y += 12;
            var lines = Wrap(g, text, F(8.5), contentW, maxLines);
            if (draw)
            {
                Tracked(g, title, F(7.5, true), Navy, Margin, y + 8, 1);
                double ly = y + 20;
                foreach (var l in lines) { g.DrawString(l, F(8.5), new XSolidBrush(Ink), Margin, ly); ly += 11; }
            }
            y += 11 + lines.Count * 11;
        }
        if (doc.Notes.T().Length > 0) Block("NOTES", doc.Notes, 3);
        if (doc.Terms.T().Length > 0) Block("TERMS & CONDITIONS", doc.Terms, 4);

        // Signatures
        y += 12 + 26;
        var labels = doc.Kind switch
        {
            PrintableDoc.DocKind.Quotation => new[] { $"For {s.CompanyName}" },
            PrintableDoc.DocKind.Challan => new[] { "Prepared By", "Gate / Security", "Received By (Customer)" },
            _ => new[] { "Received By", $"For {s.CompanyName}" },
        };
        if (draw)
        {
            double w = labels.Length == 1 ? 190 : (contentW - 30 * (labels.Length - 1)) / labels.Length;
            double x = labels.Length == 1 ? Margin + contentW - 190 : Margin;
            foreach (var label in labels)
            {
                g.DrawLine(new XPen(XColor.FromArgb(153, 31, 33, 41), 0.7), x, y, x + w, y);
                Centered(g, label, F(8.5, true), Ink, x, w, y + 13);
                Centered(g, "Signature & Stamp", F(7), Muted, x, w, y + 24);
                x += w + 30;
            }
        }
        y += 28;
        return y - top;
    }

    static void TotalRow(XGraphics g, double x, double y, string label, string value)
    {
        g.DrawString(label, F(9), new XSolidBrush(Muted), x + 10, y + 14);
        g.DrawString(value, F(9.5), new XSolidBrush(Ink), x + 210 - g.MeasureString(value, F(9.5)).Width, y + 14);
        g.DrawLine(new XPen(Rule, 0.5), x, y + 22, x + 220, y + 22);
    }

    // MARK: Text helpers

    internal static double Width(XGraphics g, string text, XFont font, double tracking) =>
        text.Sum(ch => g.MeasureString(ch.ToString(), font).Width) + tracking * Math.Max(0, text.Length - 1);

    /// <summary>Draws text with letter spacing (baseline at y).</summary>
    internal static void Tracked(XGraphics g, string text, XFont font, XColor color, double x, double y, double tracking)
    {
        var brush = new XSolidBrush(color);
        foreach (var ch in text)
        {
            var s = ch.ToString();
            g.DrawString(s, font, brush, x, y);
            x += g.MeasureString(s, font).Width + tracking;
        }
    }

    static void Centered(XGraphics g, string text, XFont font, XColor color, double x, double w, double y) =>
        g.DrawString(text, font, new XSolidBrush(color), x + (w - g.MeasureString(text, font).Width) / 2, y);

    internal static string Ellipsize(XGraphics g, string text, XFont font, double width)
    {
        if (g.MeasureString(text, font).Width <= width) return text;
        var t = text;
        while (t.Length > 1 && g.MeasureString(t + "…", font).Width > width) t = t[..^1];
        return t.TrimEnd() + "…";
    }

    internal static List<string> Wrap(XGraphics g, string text, XFont font, double width, int maxLines)
    {
        var lines = new List<string>();
        foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { if (lines.Count > 0) lines.Add(""); continue; }
            var cur = "";
            foreach (var w in words)
            {
                var next = cur.Length == 0 ? w : cur + " " + w;
                if (g.MeasureString(next, font).Width <= width || cur.Length == 0) cur = next;
                else { lines.Add(cur); cur = w; }
            }
            lines.Add(cur);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = Ellipsize(g, lines[^1] + " …", font, width);
        }
        return lines;
    }
}

/// <summary>The user's letterhead: first page of a PDF, or an image stretched to A4.</summary>
public sealed class Letterhead
{
    readonly byte[] data;
    readonly bool isPdf;

    Letterhead(byte[] data, bool isPdf) { this.data = data; this.isPdf = isPdf; }

    public static Letterhead? From(byte[]? data)
    {
        if (data is not { Length: > 8 }) return null;
        var isPdf = data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F';
        try
        {
            if (isPdf) { using var f = XPdfForm.FromStream(new MemoryStream(data)); }
            else { using var img = XImage.FromStream(new MemoryStream(data)); }
            return new Letterhead(data, isPdf);
        }
        catch { return null; }
    }

    public void Draw(XGraphics g)
    {
        if (isPdf)
        {
            using var form = XPdfForm.FromStream(new MemoryStream(data));
            double s = Math.Min(PdfDocs.PageW / form.PointWidth, PdfDocs.PageH / form.PointHeight);
            double w = form.PointWidth * s, h = form.PointHeight * s;
            g.DrawImage(form, (PdfDocs.PageW - w) / 2, (PdfDocs.PageH - h) / 2, w, h);
        }
        else
        {
            using var img = XImage.FromStream(new MemoryStream(data));
            g.DrawImage(img, 0, 0, PdfDocs.PageW, PdfDocs.PageH);
        }
    }
}

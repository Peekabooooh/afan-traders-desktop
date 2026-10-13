using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AfanTraders.Core;

/// <summary>Prints any report as landscape A4 pages (cost columns left out while hidden).</summary>
public static class ReportPdf
{
    const double W = 841.89, H = 595.28, M = 30, RowH = 16;
    static readonly XColor Navy = XColor.FromArgb(31, 59, 102), Ink = XColor.FromArgb(31, 33, 41), Muted = XColor.FromArgb(107, 115, 128);

    static XFont F(double size, bool bold = false) => new(BundledFonts.Family, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    public static byte[] Render(ReportTable table, IReadOnlyList<ReportRow> rows, string period, CompanySettings s, bool showCost)
    {
        BundledFonts.Install();
        var cols = Enumerable.Range(0, table.Columns.Count).Where(i => showCost || !table.Columns[i].Sensitive).ToList();
        var natural = cols.Sum(i => table.Columns[i].Width);
        var scale = Math.Min(1, (W - M * 2) / natural);
        var widths = cols.Select(i => table.Columns[i].Width * scale).ToList();
        var fs = Math.Max(0.8, scale);
        var totals = table.Totals(rows);
        int perPage = (int)((H - 92 - 30 - 20 - RowH) / RowH);
        var chunks = rows.Count == 0 ? new List<List<ReportRow>> { new() }
            : rows.Select((r, i) => (r, i)).GroupBy(x => x.i / perPage).Select(g => g.Select(x => x.r).ToList()).ToList();

        using var pdf = new PdfDocument();
        for (int n = 0; n < chunks.Count; n++)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(W);
            page.Height = XUnit.FromPoint(H);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString(s.CompanyName, F(15, true), new XSolidBrush(Navy), M, 40);
            PdfDocs.Tracked(g, table.Title.ToUpperInvariant(), F(11, true), Ink, M, 56, 1.2);
            var p1 = $"Period: {period}"; var p2 = $"Generated {Fmt.Date(DateTime.Now)}";
            g.DrawString(p1, F(9, true), new XSolidBrush(Ink), W - M - g.MeasureString(p1, F(9, true)).Width, 42);
            g.DrawString(p2, F(8), new XSolidBrush(Muted), W - M - g.MeasureString(p2, F(8)).Width, 55);
            g.DrawRectangle(new XSolidBrush(Navy), M, 64, W - M * 2, 2);

            double y = 76;
            g.DrawRectangle(new XSolidBrush(Navy), M, y, widths.Sum(), 20);
            DrawRow(g, cols.Select(i => new ReportCell(table.Columns[i].Title.ToUpperInvariant(), null)).ToList(), cols, table, widths, y, 20, header: true, bold: false, fs);
            y += 20;
            for (int i = 0; i < chunks[n].Count; i++)
            {
                if (i % 2 == 1) g.DrawRectangle(new XSolidBrush(XColor.FromArgb(13, 31, 59, 102)), M, y, widths.Sum(), RowH);
                DrawRow(g, cols.Select(c => chunks[n][i].Cells[c]).ToList(), cols, table, widths, y, RowH, false, false, fs);
                y += RowH;
            }
            if (n == chunks.Count - 1 && totals != null)
            {
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(31, 31, 59, 102)), M, y, widths.Sum(), RowH + 2);
                DrawRow(g, cols.Select(c => totals[c]).ToList(), cols, table, widths, y, RowH + 2, false, true, fs);
            }
            g.DrawString($"{s.CompanyName} · {table.Title}", F(7.5), new XSolidBrush(Muted), M, H - 18);
            var pn = $"Page {n + 1} of {chunks.Count}";
            g.DrawString(pn, F(7.5), new XSolidBrush(Muted), W - M - g.MeasureString(pn, F(7.5)).Width, H - 18);
        }
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }

    static void DrawRow(XGraphics g, List<ReportCell> cells, List<int> cols, ReportTable t, List<double> widths, double y, double h, bool header, bool bold, double fs)
    {
        double x = M;
        var font = F((header ? 6.5 : 7.5) * fs, header || bold);
        var brush = new XSolidBrush(header ? XColors.White : Ink);
        for (int i = 0; i < cells.Count; i++)
        {
            var w = widths[i];
            var text = PdfDocs.Ellipsize(g, cells[i].Text, font, w - 8);
            var tw = g.MeasureString(text, font).Width;
            var tx = t.Columns[cols[i]].Numeric ? x + w - 4 - tw : x + 4;
            g.DrawString(text, font, brush, tx, y + h / 2 + 2.6);
            x += w;
        }
    }
}

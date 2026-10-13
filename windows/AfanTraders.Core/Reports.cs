using System.Globalization;
using System.Text;

namespace AfanTraders.Core;

// Port of macOS Reports/ReportModel.swift + ReportEngine.swift.

public enum CellKind { Text, Money, Qty, Percent, Date, Count }

public abstract record ReportTotal
{
    public sealed record None : ReportTotal;
    public sealed record Label(string Text) : ReportTotal;
    public sealed record Sum : ReportTotal;
    public sealed record CountRows : ReportTotal;
    public sealed record Ratio(int Num, int Den, bool Percent) : ReportTotal;
}

public sealed record ReportColumn(string Title, double Width = 100, CellKind Kind = CellKind.Text, bool Sensitive = false, ReportTotal? Total = null)
{
    public bool Numeric => Kind is not (CellKind.Text or CellKind.Date);
    public ReportTotal TotalKind => Total ?? new ReportTotal.None();
}

public sealed record ReportCell(string Text, double? Value)
{
    public static ReportCell T(string s) => new(string.IsNullOrEmpty(s) ? "—" : s, null);
    public static ReportCell M(double v) => new(Fmt.Money(v), Fmt.R2(v));
    public static ReportCell Q(double v) => new(Fmt.Qty(v), v);
    public static ReportCell N(int v) => new(v.ToString(CultureInfo.InvariantCulture), v);
    public static ReportCell P(double? v) => v is { } x && double.IsFinite(x) ? new($"{Fmt.Qty(Fmt.R2(x))}%", Fmt.R2(x)) : new("—", null);
    public static ReportCell D(DateTime d) => new(Fmt.Date(d), Fmt.Unix(d));
}

/// <summary>What double-clicking a report row opens.</summary>
public sealed record OpenTarget(string Kind, long Id);

public sealed record ReportRow(IReadOnlyList<ReportCell> Cells, OpenTarget? Open = null);

public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows)
{
    public static double? Margin(double profit, double sales) => sales == 0 ? null : profit / sales * 100;

    public IReadOnlyList<ReportCell>? Totals(IReadOnlyList<ReportRow> rows)
    {
        if (Columns.All(c => c.TotalKind is ReportTotal.None)) return null;
        double Sum(int i) => rows.Sum(r => r.Cells[i].Value ?? 0);
        return Columns.Select((col, i) => col.TotalKind switch
        {
            ReportTotal.Label l => new ReportCell(l.Text, null),
            ReportTotal.CountRows => new ReportCell($"TOTAL · {rows.Count}", rows.Count),
            ReportTotal.Sum => col.Kind switch { CellKind.Qty => ReportCell.Q(Sum(i)), CellKind.Count => ReportCell.N((int)Sum(i)), _ => ReportCell.M(Sum(i)) },
            ReportTotal.Ratio r => Sum(r.Den) == 0 ? new ReportCell("—", null)
                : r.Percent ? ReportCell.P(Sum(r.Num) / Sum(r.Den) * 100) : ReportCell.M(Sum(r.Num) / Sum(r.Den)),
            _ => new ReportCell("", null),
        }).ToList();
    }

    public IReadOnlyList<ReportRow> FilteredSorted(string search, int? sort, bool ascending)
    {
        var q = search.T();
        IEnumerable<ReportRow> rows = q.Length == 0 ? Rows : Rows.Where(r => r.Cells.Any(c => c.Text.Contains(q, StringComparison.OrdinalIgnoreCase)));
        if (sort is { } s && s < Columns.Count)
        {
            var list = rows.ToList();
            list.Sort((a, b) =>
            {
                var x = a.Cells[s]; var y = b.Cells[s];
                int c = x.Value is { } xv && y.Value is { } yv ? xv.CompareTo(yv)
                      : x.Value != null ? 1 : y.Value != null ? -1
                      : string.Compare(x.Text, y.Text, StringComparison.CurrentCultureIgnoreCase);
                return ascending ? c : -c;
            });
            return list;
        }
        return rows.ToList();
    }

    /// <summary>Excel-friendly CSV (UTF-8 with BOM). Hidden cost columns are left out.</summary>
    public string Csv(IReadOnlyList<ReportRow> rows, bool includeSensitive)
    {
        var keep = Enumerable.Range(0, Columns.Count).Where(i => includeSensitive || !Columns[i].Sensitive).ToList();
        static string Esc(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        string Field(ReportCell c, ReportColumn col) =>
            col.Numeric && c.Value is { } v ? (col.Kind == CellKind.Count ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture))
            : Esc(c.Text == "—" ? "" : c.Text);
        var sb = new StringBuilder("﻿");
        sb.Append(string.Join(",", keep.Select(i => Esc(Columns[i].Title)))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", keep.Select(i => Field(r.Cells[i], Columns[i])))).Append("\r\n");
        if (Totals(rows) is { } t) sb.Append(string.Join(",", keep.Select(i => Field(t[i], Columns[i])))).Append("\r\n");
        return sb.ToString();
    }
}

public enum RangePreset { Today, Yesterday, ThisWeek, ThisMonth, LastMonth, Last30, ThisYear, AllTime, Custom }

public static class Ranges
{
    public static string Title(this RangePreset p) => p switch
    {
        RangePreset.Today => "Today", RangePreset.Yesterday => "Yesterday", RangePreset.ThisWeek => "This Week",
        RangePreset.ThisMonth => "This Month", RangePreset.LastMonth => "Last Month", RangePreset.Last30 => "Last 30 Days",
        RangePreset.ThisYear => "This Year", RangePreset.AllTime => "All Time", _ => "Custom",
    };

    public static (DateTime Start, DateTime End) Interval(this RangePreset p, DateTime from, DateTime to)
    {
        var today = DateTime.Today;
        DateTime EndOf(DateTime d) => d.Date.AddDays(1).AddSeconds(-1);
        return p switch
        {
            RangePreset.Today => (today, EndOf(today)),
            RangePreset.Yesterday => (today.AddDays(-1), EndOf(today.AddDays(-1))),
            RangePreset.ThisWeek => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), EndOf(today)),
            RangePreset.ThisMonth => (new DateTime(today.Year, today.Month, 1), EndOf(today)),
            RangePreset.LastMonth => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddSeconds(-1)),
            RangePreset.Last30 => (today.AddDays(-29), EndOf(today)),
            RangePreset.ThisYear => (new DateTime(today.Year, 1, 1), EndOf(today)),
            RangePreset.AllTime => (new DateTime(1970, 1, 1), EndOf(today).AddYears(50)),
            _ => (from < to ? from.Date : to.Date, EndOf(from < to ? to : from)),
        };
    }

    public static bool In(this (DateTime Start, DateTime End) r, DateTime d) => d >= r.Start && d <= r.End;
}

public sealed record SaleLine(Invoice Invoice, DocLine Line, double UnitCost)
{
    public double Amount => Line.Total;
    public double Cost => Fmt.R2(UnitCost * Line.Qty);
    public double Profit => Fmt.R2(Amount - Cost);
}

public sealed record ChartPoint(DateTime Date, double Sales, double Profit);

public sealed class Overview
{
    public double NetSales, Tax, Total, Cost, Profit, Qty, Purchases, OpenQuotesValue, StockCost, StockSale;
    public int Bills, PurchaseBills, OpenQuotes;
    public List<ChartPoint> Points = new();
    public bool ByMonth;
    public List<(string Name, double Sales, double Profit)> TopItems = new(), TopCustomers = new();
    public double? Margin => ReportTable.Margin(Profit, NetSales);
    public double AvgBill => Bills == 0 ? 0 : Total / Bills;
}

public sealed partial class Store
{
    public double UnitCost(DocLine l) => l.Cost > 0 ? l.Cost : ItemById(l.ItemId)?.CostPrice ?? 0;
    public List<Invoice> InvoicesIn((DateTime, DateTime) r) => Invoices.Where(i => r.In(i.Date)).ToList();
    public List<Purchase> PurchasesIn((DateTime, DateTime) r) => Purchases.Where(p => r.In(p.Date)).ToList();
    public List<SaleLine> SaleLines((DateTime, DateTime) r) => InvoicesIn(r).SelectMany(i => i.Lines.Select(l => new SaleLine(i, l, UnitCost(l)))).ToList();
    public double CostOf(Invoice inv) => Fmt.R2(inv.Lines.Sum(l => UnitCost(l) * l.Qty));
    static string Key(DocLine l) => l.ItemId is { } id ? $"#{id}" : l.Code.ToLowerInvariant();

    public Overview GetOverview((DateTime Start, DateTime End) r)
    {
        var o = new Overview();
        var invs = InvoicesIn(r);
        var lines = SaleLines(r);
        o.Bills = invs.Count;
        o.NetSales = invs.Sum(i => i.Subtotal());
        o.Tax = invs.Sum(i => i.TaxAmount());
        o.Total = invs.Sum(i => i.GrandTotal());
        o.Cost = lines.Sum(l => l.Cost);
        o.Profit = o.NetSales - o.Cost;
        o.Qty = lines.Sum(l => l.Line.Qty);
        var ps = PurchasesIn(r);
        o.Purchases = ps.Sum(p => p.Total);
        o.PurchaseBills = ps.Count;
        var open = Quotations.Where(q => StageOf(q) != OrderStage.Invoiced).ToList();
        o.OpenQuotes = open.Count;
        o.OpenQuotesValue = open.Sum(q => q.Subtotal());
        o.StockCost = Items.Sum(i => Math.Max(0, i.Stock) * i.CostPrice);
        o.StockSale = Items.Sum(i => Math.Max(0, i.Stock) * i.SalePrice);

        var first = invs.Count > 0 ? invs.Min(i => i.Date) : r.Start;
        var start = first > r.Start ? first : r.Start;
        var end = r.End < DateTime.Now ? r.End : DateTime.Now;
        o.ByMonth = (end - start).TotalDays > 62;
        o.Points = lines.GroupBy(l => o.ByMonth ? new DateTime(l.Invoice.Date.Year, l.Invoice.Date.Month, 1) : l.Invoice.Date.Date)
            .Select(g => new ChartPoint(g.Key, g.Sum(x => x.Amount), g.Sum(x => x.Profit))).OrderBy(p => p.Date).ToList();
        o.TopItems = lines.GroupBy(l => Key(l.Line)).Select(g => ($"{g.First().Line.Code} – {g.First().Line.Name}", g.Sum(x => x.Amount), g.Sum(x => x.Profit)))
            .OrderByDescending(x => x.Item2).Take(6).ToList();
        o.TopCustomers = invs.GroupBy(i => i.CustomerName.ToLowerInvariant()).Select(g => (g.First().CustomerName, g.Sum(i => i.Subtotal()), g.Sum(i => i.Subtotal() - CostOf(i))))
            .OrderByDescending(x => x.Item2).Take(6).ToList();
        return o;
    }

    static ReportColumn C(string t, double w = 100, CellKind k = CellKind.Text, bool s = false, ReportTotal? tot = null) => new(t, w, k, s, tot);
    static readonly ReportTotal Sum = new ReportTotal.Sum();
    static readonly ReportTotal Count = new ReportTotal.CountRows();
    static ReportTotal Lbl(string s) => new ReportTotal.Label(s);
    static ReportTotal Pct(int n, int d) => new ReportTotal.Ratio(n, d, true);

    public ReportTable DailyReport((DateTime, DateTime) r)
    {
        var days = new SortedDictionary<DateTime, double[]>();   // bills, qty, net, tax, total, cost, purchases
        double[] D(DateTime d) => days.TryGetValue(d.Date, out var v) ? v : days[d.Date] = new double[7];
        foreach (var inv in InvoicesIn(r))
        {
            var v = D(inv.Date);
            v[0]++; v[1] += inv.Lines.Sum(l => l.Qty); v[2] += inv.Subtotal(); v[3] += inv.TaxAmount(); v[4] += inv.GrandTotal(); v[5] += CostOf(inv);
        }
        foreach (var p in PurchasesIn(r)) D(p.Date)[6] += p.Total;
        var rows = days.Reverse().Select(kv => new ReportRow(new[]
        {
            ReportCell.D(kv.Key), ReportCell.N((int)kv.Value[0]), ReportCell.Q(kv.Value[1]), ReportCell.M(kv.Value[2]), ReportCell.M(kv.Value[3]),
            ReportCell.M(kv.Value[4]), ReportCell.M(kv.Value[5]), ReportCell.M(kv.Value[2] - kv.Value[5]),
            ReportCell.P(ReportTable.Margin(kv.Value[2] - kv.Value[5], kv.Value[2])), ReportCell.M(kv.Value[6]),
        })).ToList();
        return new("Day by Day", new[]
        {
            C("Date", 100, CellKind.Date, tot: Lbl("TOTAL")), C("Bills", 60, CellKind.Count, tot: Sum), C("Qty Sold", 80, CellKind.Qty, tot: Sum),
            C("Net Sales", 105, CellKind.Money, tot: Sum), C("Sales Tax", 100, CellKind.Money, tot: Sum), C("Total Billed", 115, CellKind.Money, tot: Sum),
            C("Cost", 100, CellKind.Money, true, Sum), C("Profit", 100, CellKind.Money, true, Sum), C("Margin", 75, CellKind.Percent, true, Pct(7, 3)),
            C("Purchases", 100, CellKind.Money, true, Sum),
        }, rows);
    }

    public ReportTable BillsReport((DateTime, DateTime) r)
    {
        var rows = InvoicesIn(r).Select(inv =>
        {
            var c = CostOf(inv);
            return new ReportRow(new[]
            {
                ReportCell.T(inv.Number), ReportCell.D(inv.Date), ReportCell.T(inv.CustomerName), ReportCell.T(inv.PoNumber),
                ReportCell.Q(inv.Lines.Sum(l => l.Qty)), ReportCell.M(inv.Subtotal()), ReportCell.M(inv.TaxAmount()), ReportCell.M(inv.GrandTotal()),
                ReportCell.M(c), ReportCell.M(inv.Subtotal() - c), ReportCell.P(ReportTable.Margin(inv.Subtotal() - c, inv.Subtotal())),
                ReportCell.T(QuotationById(inv.QuotationId)?.Number ?? ""), ReportCell.T(ChallanById(inv.ChallanId)?.Number ?? ""),
            }, new OpenTarget("invoice", inv.Id));
        }).ToList();
        return new("Bill by Bill (Sales Invoices)", new[]
        {
            C("Invoice No.", 115, tot: Count), C("Date", 95, CellKind.Date), C("Customer", 170), C("PO No.", 95), C("Qty", 60, CellKind.Qty, tot: Sum),
            C("Net Sales", 100, CellKind.Money, tot: Sum), C("Sales Tax", 100, CellKind.Money, tot: Sum), C("Total", 105, CellKind.Money, tot: Sum),
            C("Cost", 100, CellKind.Money, true, Sum), C("Profit", 100, CellKind.Money, true, Sum), C("Margin", 70, CellKind.Percent, true, Pct(9, 5)),
            C("Quotation", 105), C("DC No.", 105),
        }, rows);
    }

    public ReportTable LineReport((DateTime, DateTime) r)
    {
        var rows = SaleLines(r).Select(s => new ReportRow(new[]
        {
            ReportCell.D(s.Invoice.Date), ReportCell.T(s.Invoice.Number), ReportCell.T(s.Invoice.CustomerName), ReportCell.T(s.Line.Code),
            ReportCell.T(s.Line.Name), ReportCell.Q(s.Line.Qty), ReportCell.M(s.Line.Rate), ReportCell.M(s.Amount), ReportCell.M(s.UnitCost),
            ReportCell.M(s.Cost), ReportCell.M(s.Profit), ReportCell.P(ReportTable.Margin(s.Profit, s.Amount)), ReportCell.M(s.Line.BaseRate),
        }, new OpenTarget("invoice", s.Invoice.Id))).ToList();
        return new("Line Detail (every item on every bill)", new[]
        {
            C("Date", 100, CellKind.Date, tot: Lbl("TOTAL")), C("Invoice No.", 120, tot: Count), C("Customer", 160), C("Item ID", 100), C("Item", 170),
            C("Qty", 70, CellKind.Qty, tot: Sum), C("Sale Rate", 100, CellKind.Money), C("Amount", 100, CellKind.Money, tot: Sum),
            C("Unit Cost", 100, CellKind.Money, true), C("Cost", 100, CellKind.Money, true, Sum), C("Profit", 100, CellKind.Money, true, Sum),
            C("Margin", 75, CellKind.Percent, true, Pct(10, 7)), C("Quoted Rate", 100, CellKind.Money),
        }, rows);
    }

    public ReportTable ItemsReport((DateTime, DateTime) r)
    {
        var bought = new Dictionary<long, double>();
        foreach (var p in PurchasesIn(r)) foreach (var l in p.Lines) if (l.ItemId is { } id) bought[id] = bought.GetValueOrDefault(id) + l.Qty;
        var rows = SaleLines(r).GroupBy(s => Key(s.Line)).Select(g =>
        {
            var f = g.First().Line;
            double qty = g.Sum(x => x.Line.Qty), sales = g.Sum(x => x.Amount), cost = g.Sum(x => x.Cost);
            var it = ItemById(f.ItemId);
            return (sales, row: new ReportRow(new[]
            {
                ReportCell.T(f.Code), ReportCell.T(f.Name), ReportCell.T(f.Unit), ReportCell.Q(qty), ReportCell.N(g.Select(x => x.Invoice.Id).Distinct().Count()),
                ReportCell.M(sales), ReportCell.M(qty == 0 ? 0 : sales / qty), ReportCell.M(cost), ReportCell.M(sales - cost),
                ReportCell.P(ReportTable.Margin(sales - cost, sales)), ReportCell.Q(f.ItemId is { } id ? bought.GetValueOrDefault(id) : 0),
                it != null ? ReportCell.Q(it.Stock) : ReportCell.T(""),
            }, it != null ? new OpenTarget("item", it.Id) : null));
        }).OrderByDescending(x => x.sales).Select(x => x.row).ToList();
        return new("Item by Item (Sales)", new[]
        {
            C("Item ID", 110, tot: Count), C("Item", 200), C("Unit", 60), C("Qty Sold", 85, CellKind.Qty, tot: Sum), C("Bills", 60, CellKind.Count),
            C("Net Sales", 100, CellKind.Money, tot: Sum), C("Avg Rate", 100, CellKind.Money, tot: new ReportTotal.Ratio(5, 3, false)),
            C("Cost", 100, CellKind.Money, true, Sum), C("Profit", 100, CellKind.Money, true, Sum), C("Margin", 80, CellKind.Percent, true, Pct(8, 5)),
            C("Purchased", 90, CellKind.Qty, tot: Sum), C("In Stock", 85, CellKind.Qty),
        }, rows);
    }

    public ReportTable CustomersReport((DateTime, DateTime) r)
    {
        var open = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in Quotations.Where(q => StageOf(q) != OrderStage.Invoiced)) open[q.CustomerName] = open.GetValueOrDefault(q.CustomerName) + q.Subtotal();
        var rows = InvoicesIn(r).GroupBy(i => i.CustomerName.ToLowerInvariant()).Select(g =>
        {
            var name = g.First().CustomerName;
            double net = g.Sum(i => i.Subtotal()), cost = g.Sum(CostOf);
            return (net, row: new ReportRow(new[]
            {
                ReportCell.T(name), ReportCell.N(g.Count()), ReportCell.Q(g.Sum(i => i.Lines.Sum(l => l.Qty))), ReportCell.M(net),
                ReportCell.M(g.Sum(i => i.TaxAmount())), ReportCell.M(g.Sum(i => i.GrandTotal())), ReportCell.M(net - cost),
                ReportCell.P(ReportTable.Margin(net - cost, net)), ReportCell.D(g.Max(i => i.Date)), ReportCell.M(open.GetValueOrDefault(name)),
            }));
        }).OrderByDescending(x => x.net).Select(x => x.row).ToList();
        return new("Customers", new[]
        {
            C("Customer", 230, tot: Count), C("Bills", 60, CellKind.Count, tot: Sum), C("Qty", 75, CellKind.Qty, tot: Sum),
            C("Net Sales", 100, CellKind.Money, tot: Sum), C("Sales Tax", 100, CellKind.Money, tot: Sum), C("Total", 115, CellKind.Money, tot: Sum),
            C("Profit", 100, CellKind.Money, true, Sum), C("Margin", 80, CellKind.Percent, true, Pct(6, 3)), C("Last Bill", 100, CellKind.Date),
            C("Open Quotations", 130, CellKind.Money, tot: Sum),
        }, rows);
    }

    public ReportTable OrdersReport((DateTime, DateTime) r)
    {
        var rows = Quotations.Where(q => r.In(q.Date)).Select(q =>
        {
            var inv = InvoiceForQuotation(q.Id);
            return new ReportRow(new[]
            {
                ReportCell.T(q.Number), ReportCell.D(q.Date), ReportCell.T(q.CustomerName), ReportCell.T(q.PoNumber), ReportCell.M(q.Subtotal()),
                ReportCell.T(StageOf(q).Short()), ReportCell.T(ChallanForQuotation(q.Id)?.Number ?? ""), ReportCell.T(inv?.Number ?? ""),
                inv != null ? ReportCell.M(inv.Subtotal()) : ReportCell.T(""), inv != null ? ReportCell.M(inv.Subtotal() - q.Subtotal()) : ReportCell.T(""),
            }, new OpenTarget("quotation", q.Id));
        }).ToList();
        return new("Orders (Quotation → Challan → Invoice)", new[]
        {
            C("Quotation", 120, tot: Count), C("Date", 100, CellKind.Date), C("Customer", 200), C("PO No.", 100),
            C("Quoted (net)", 120, CellKind.Money, tot: Sum), C("Status", 120), C("Delivery Challan", 120), C("Invoice", 120),
            C("Invoiced (net)", 120, CellKind.Money, tot: Sum), C("Price Change", 110, CellKind.Money, tot: Sum),
        }, rows);
    }

    public ReportTable PurchasesReport((DateTime, DateTime) r)
    {
        var rows = PurchasesIn(r).Select(p => new ReportRow(new[]
        {
            ReportCell.D(p.Date), ReportCell.T(p.Number), ReportCell.T(p.Supplier), ReportCell.N(p.Lines.Count), ReportCell.Q(p.Lines.Sum(l => l.Qty)),
            ReportCell.T(string.Join(", ", p.Lines.Select(l => l.Code))), ReportCell.M(p.Total),
        }, new OpenTarget("purchase", p.Id))).ToList();
        return new("Purchase Invoices (Stock In)", new[]
        {
            C("Date", 100, CellKind.Date, tot: Lbl("TOTAL")), C("Invoice No.", 130, tot: Count), C("Supplier", 200), C("Lines", 60, CellKind.Count, tot: Sum),
            C("Qty", 80, CellKind.Qty, tot: Sum), C("Items", 300), C("Total Cost", 120, CellKind.Money, true, Sum),
        }, rows);
    }

    public ReportTable StockReport((DateTime, DateTime) r)
    {
        var inQ = new Dictionary<long, double>(); var outQ = new Dictionary<long, double>();
        foreach (var p in PurchasesIn(r)) foreach (var l in p.Lines) if (l.ItemId is { } id) inQ[id] = inQ.GetValueOrDefault(id) + l.Qty;
        foreach (var c in Challans.Where(c => c.Status == ChallanStatus.Approved && r.In(c.ApprovedAt ?? c.Date)))
            foreach (var l in c.Lines) if (l.ItemId is { } id) outQ[id] = outQ.GetValueOrDefault(id) + l.Qty;
        var rows = Items.Select(it =>
        {
            var s = Math.Max(0, it.Stock);
            return new ReportRow(new[]
            {
                ReportCell.T(it.Code), ReportCell.T(it.Name), ReportCell.T(it.Unit), ReportCell.Q(it.Stock), ReportCell.M(it.CostPrice), ReportCell.M(it.SalePrice),
                ReportCell.M(s * it.CostPrice), ReportCell.M(s * it.SalePrice), ReportCell.M(s * (it.SalePrice - it.CostPrice)),
                ReportCell.Q(inQ.GetValueOrDefault(it.Id)), ReportCell.Q(outQ.GetValueOrDefault(it.Id)),
            }, new OpenTarget("item", it.Id));
        }).ToList();
        return new("Stock & Valuation", new[]
        {
            C("Item ID", 110, tot: Count), C("Item", 200), C("Unit", 60), C("In Stock", 85, CellKind.Qty, tot: Sum),
            C("Unit Cost", 100, CellKind.Money, true), C("Sale Price", 100, CellKind.Money), C("Value @ Cost", 120, CellKind.Money, true, Sum),
            C("Value @ Sale", 120, CellKind.Money, tot: Sum), C("Potential Profit", 125, CellKind.Money, true, Sum),
            C("In (period)", 90, CellKind.Qty, tot: Sum), C("Out (period)", 95, CellKind.Qty, tot: Sum),
        }, rows);
    }
}

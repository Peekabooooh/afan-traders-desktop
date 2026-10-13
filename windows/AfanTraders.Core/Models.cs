namespace AfanTraders.Core;

// Same records as the Mac app (macOS/Sources/AfanTraders/Data/Models.swift).

public sealed record Item
{
    public long Id { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Details { get; init; } = "";
    public string Unit { get; init; } = "Pcs";
    public byte[]? Image { get; init; }
    public double CostPrice { get; init; }
    public double SalePrice { get; init; }
    public double Stock { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

/// <summary>A line on a quotation, delivery challan or invoice (item details are copied).</summary>
public sealed record DocLine
{
    public Guid Key { get; init; } = Guid.NewGuid();
    public long? ItemId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Details { get; init; } = "";
    public string Unit { get; init; } = "Pcs";
    public double Qty { get; init; } = 1;
    public double Rate { get; init; }
    /// <summary>The quotation rate this line started from (invoices show margin against it).</summary>
    public double BaseRate { get; init; }
    /// <summary>Unit cost when invoiced (0 = unknown, use the item's current cost).</summary>
    public double Cost { get; init; }

    public double Total => Fmt.R2(Qty * Rate);
}

public interface IPriced
{
    IReadOnlyList<DocLine> Lines { get; }
    double TaxRate { get; }
}

public static class PricedExtensions
{
    public static double Subtotal(this IPriced d) => Fmt.R2(d.Lines.Sum(l => l.Total));
    public static double TaxAmount(this IPriced d) => Fmt.R2(d.Subtotal() * d.TaxRate / 100);
    public static double GrandTotal(this IPriced d) => Fmt.R2(d.Subtotal() + d.TaxAmount());
}

public sealed record Quotation : IPriced
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public string PoNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string CustomerAddress { get; init; } = "";
    public string CustomerPhone { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public double TaxRate { get; init; } = 18;
    public string Notes { get; init; } = "";
    public IReadOnlyList<DocLine> Lines { get; init; } = Array.Empty<DocLine>();
}

public enum ChallanStatus { Pending, Approved }

public sealed record Challan
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public long QuotationId { get; init; }
    public string PoNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string CustomerAddress { get; init; } = "";
    public string CustomerPhone { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public ChallanStatus Status { get; init; } = ChallanStatus.Pending;
    public DateTime? ApprovedAt { get; init; }
    public string VehicleNo { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string Notes { get; init; } = "";
    public IReadOnlyList<DocLine> Lines { get; init; } = Array.Empty<DocLine>();

    public double TotalQty => Lines.Sum(l => l.Qty);
}

public sealed record Invoice : IPriced
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public long QuotationId { get; init; }
    public long ChallanId { get; init; }
    public string PoNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string CustomerAddress { get; init; } = "";
    public string CustomerPhone { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public double TaxRate { get; init; } = 18;
    public string Notes { get; init; } = "";
    public IReadOnlyList<DocLine> Lines { get; init; } = Array.Empty<DocLine>();
}

public sealed record PurchaseLine
{
    public Guid Key { get; init; } = Guid.NewGuid();
    public long? ItemId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Details { get; init; } = "";
    public string Unit { get; init; } = "Pcs";
    public double Qty { get; init; } = 1;
    public double Cost { get; init; }
    public double SalePrice { get; init; }
    /// <summary>Optional picture for a new item (saved on the item).</summary>
    public byte[]? Image { get; init; }

    public double Total => Fmt.R2(Qty * Cost);
    public bool IsBlank => string.IsNullOrWhiteSpace(Code) && string.IsNullOrWhiteSpace(Name);
}

public sealed record Purchase
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public string Supplier { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public string Notes { get; init; } = "";
    public IReadOnlyList<PurchaseLine> Lines { get; init; } = Array.Empty<PurchaseLine>();

    public double Total => Fmt.R2(Lines.Sum(l => l.Total));
}

public sealed record CompanySettings
{
    public string CompanyName { get; init; } = "AFAN TRADERS";
    public string Tagline { get; init; } = "General Order Suppliers";
    public string Address { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Ntn { get; init; } = "";
    public string Strn { get; init; } = "";
    public byte[]? Logo { get; init; }

    public double DefaultTaxRate { get; init; } = 18;
    public string Currency { get; init; } = "Rs.";
    public string CurrencyWord { get; init; } = "Rupees";

    public string QuotationPrefix { get; init; } = "QT";
    public string ChallanPrefix { get; init; } = "DC";
    public string InvoicePrefix { get; init; } = "INV";
    public string InvoiceTitle { get; init; } = "SALES TAX INVOICE";

    public string QuotationTerms { get; init; } = "Prices are valid for 15 days.\nDelivery within 7 working days after PO.";
    public string InvoiceTerms { get; init; } = "Payment due within 30 days.\nCheques in favour of AFAN TRADERS.";

    public byte[]? Letterhead { get; init; }
    public double LetterheadTop { get; init; } = 150;
    public double LetterheadBottom { get; init; } = 90;

    public bool SameAs(CompanySettings o) =>
        this with { Logo = null, Letterhead = null } == o with { Logo = null, Letterhead = null }
        && Bytes.Same(Logo, o.Logo) && Bytes.Same(Letterhead, o.Letterhead);
}

public enum OrderStage { NeedsChallan, AwaitingApproval, ReadyToInvoice, Invoiced }

public static class OrderStageText
{
    public static string Title(this OrderStage s) => s switch
    {
        OrderStage.NeedsChallan => "Needs Delivery Challan",
        OrderStage.AwaitingApproval => "Challan Pending Approval",
        OrderStage.ReadyToInvoice => "Ready to Invoice",
        _ => "Invoiced",
    };

    public static string Short(this OrderStage s) => s switch
    {
        OrderStage.NeedsChallan => "Needs DC",
        OrderStage.AwaitingApproval => "DC Pending",
        OrderStage.ReadyToInvoice => "Ready to Invoice",
        _ => "Invoiced",
    };
}

public sealed class AppError : Exception
{
    public AppError(string message) : base(message) { }
}

static class Bytes
{
    public static bool Same(byte[]? a, byte[]? b) =>
        a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
}

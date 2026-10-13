using System.Globalization;
using System.Text;

namespace AfanTraders.Core;

public static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static double R2(double v) => Math.Round(v * 100, MidpointRounding.AwayFromZero) / 100;

    public static string Money(double v) => v.ToString("#,##0.00", Inv);
    public static string Money(double v, string currency) => $"{currency} {Money(v)}";
    public static string Qty(double v) => v.ToString("#,##0.###", Inv);
    public static string Date(DateTime d) => d.ToString("dd-MMM-yyyy", Inv);

    /// <summary>Plain number for editing (no grouping, no trailing zeros).</summary>
    public static string Editable(double v) => v.ToString("0.####", Inv);

    public static double? Parse(string? text)
    {
        var t = (text ?? "").Replace(",", "").Trim();
        return double.TryParse(t, NumberStyles.Float, Inv, out var v) ? v : null;
    }

    public static string T(this string? s) => (s ?? "").Trim();

    /// <summary>"Rupees Twelve Thousand Five Hundred Only"</summary>
    public static string Words(double amount, string currencyWord)
    {
        var a = R2(amount);
        var whole = (long)Math.Floor(a);
        var fraction = (int)Math.Round((a - whole) * 100);
        var s = Spell(whole);
        if (currencyWord.Length > 0) s = $"{currencyWord} {s}";
        if (fraction > 0)
        {
            var unit = currencyWord.Contains("rupee", StringComparison.OrdinalIgnoreCase) ? "Paisa" : "Cents";
            s += $" and {Spell(fraction)} {unit}";
        }
        return s + " Only";
    }

    static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven",
        "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };
    static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    /// <summary>English number words (short scale, like the Mac app).</summary>
    public static string Spell(long n)
    {
        if (n == 0) return "Zero";
        if (n < 0) return "Minus " + Spell(-n);
        var parts = new List<string>();
        (long value, string name)[] scales = { (1_000_000_000_000, "Trillion"), (1_000_000_000, "Billion"), (1_000_000, "Million"), (1_000, "Thousand") };
        foreach (var (value, name) in scales)
        {
            if (n >= value) { parts.Add($"{Below1000((int)(n / value))} {name}"); n %= value; }
        }
        if (n > 0) parts.Add(Below1000((int)n));
        return string.Join(" ", parts);
    }

    static string Below1000(int n)
    {
        var sb = new StringBuilder();
        if (n >= 100) { sb.Append(Ones[n / 100]).Append(" Hundred"); n %= 100; if (n > 0) sb.Append(' '); }
        if (n >= 20) { sb.Append(Tens[n / 10]); if (n % 10 > 0) sb.Append('-').Append(Ones[n % 10]); }
        else if (n > 0) sb.Append(Ones[n]);
        return sb.ToString();
    }

    public static double Unix(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds() / 1000.0;
    public static DateTime FromUnix(double s) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(s * 1000)).LocalDateTime;
}

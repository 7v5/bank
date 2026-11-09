using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Html;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Infrastructure;

/// <summary>Small presentation helpers shared by views: icons, labels, relative times and the banknote engraving.</summary>
public static class Ui
{
    public static IHtmlContent Icon(string name, string? extraClass = null) =>
        new HtmlString($"<svg class=\"icon {extraClass}\" aria-hidden=\"true\"><use href=\"#i-{name}\"/></svg>");

    public static string CategoryIcon(TransactionCategory c) => c switch
    {
        TransactionCategory.Income => "briefcase",
        TransactionCategory.Groceries => "cart",
        TransactionCategory.Dining => "utensils",
        TransactionCategory.Bills => "receipt",
        TransactionCategory.Shopping => "bag",
        TransactionCategory.Transport => "car",
        TransactionCategory.Entertainment => "ticket",
        TransactionCategory.People => "users",
        TransactionCategory.Transfer => "swap",
        TransactionCategory.Cash => "cash",
        _ => "dots"
    };

    public static string TransactionIcon(Transaction t) => t.Type switch
    {
        TransactionType.TransferIn or TransactionType.TransferOut => "swap",
        TransactionType.P2PSent => "send",
        TransactionType.P2PReceived => "arrow-down",
        TransactionType.Deposit when t.Category == TransactionCategory.Cash => "arrow-down",
        TransactionType.Withdrawal => "cash",
        TransactionType.AdminAdjustment => "shield",
        _ => CategoryIcon(t.Category)
    };

    public static string TypeLabel(TransactionType t) => t switch
    {
        TransactionType.TransferIn => "Transfer in",
        TransactionType.TransferOut => "Transfer out",
        TransactionType.BillPayment => "Bill payment",
        TransactionType.P2PSent => "Sent",
        TransactionType.P2PReceived => "Received",
        TransactionType.Purchase => "Card purchase",
        TransactionType.AdminAdjustment => "Adjustment",
        _ => t.ToString()
    };

    /// <summary>Title shown on a transaction row: the counterparty when there is one, otherwise the description.</summary>
    public static string TransactionTitle(Transaction t) => t.Type switch
    {
        TransactionType.P2PSent => $"To {t.CounterpartyName}",
        TransactionType.P2PReceived => $"From {t.CounterpartyName}",
        TransactionType.Purchase or TransactionType.BillPayment when t.CounterpartyName is not null => t.CounterpartyName,
        _ => t.Description
    };

    public static string AccountTypeLabel(AccountType t) => t switch
    {
        AccountType.Checking => "Checking",
        AccountType.Savings => "Savings",
        _ => t.ToString()
    };

    public static string AccountIcon(BankAccount a) => a.Type switch
    {
        AccountType.Savings => "vault",
        _ => "wallet"
    };

    public static string DayLabel(DateTime utc, DateTime nowUtc)
    {
        var d = utc.Date;
        if (d == nowUtc.Date) return "Today";
        if (d == nowUtc.Date.AddDays(-1)) return "Yesterday";
        return d.Year == nowUtc.Year ? d.ToString("dddd, MMMM d", Money.Culture) : d.ToString("MMMM d, yyyy", Money.Culture);
    }

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToString(utc.Year == nowUtc.Year ? "MMM d" : "MMM d, yyyy", Money.Culture);
    }

    public static string Greeting(DateTime localNow) => localNow.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };

    public static string Dollars(decimal amount) => Math.Truncate(Math.Abs(amount)).ToString("N0", Money.Culture);
    public static string Cents(decimal amount) => ((int)(Math.Abs(amount) % 1 * 100)).ToString("00");

    public static string UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ---------- Engraving ----------

    private static readonly Lazy<string> RosettePaths = new(BuildRosette);
    private static readonly Lazy<string> WavePaths = new(BuildWaves);

    /// <summary>Guilloché rosette (layered hypotrochoids) like the engraving on a banknote. Centered at 0,0, radius ≈ 100.</summary>
    public static IHtmlContent Rosette() => new HtmlString(RosettePaths.Value);

    /// <summary>Interference wave band, 400 × 60.</summary>
    public static IHtmlContent Waves() => new HtmlString(WavePaths.Value);

    private static string BuildRosette()
    {
        var sb = new StringBuilder();
        // (R, r, d, opacity) — each layer is a closed spirograph curve.
        (double R, double r, double d, double o)[] layers = [(90, 33, 46, 0.85), (76, 28, 34, 0.6), (52, 20, 24, 0.5), (30, 12, 13, 0.45)];
        var i = 0;
        foreach (var (R, r, d, o) in layers)
        {
            var turns = r / Gcd((int)R, (int)r);
            var steps = (int)(150 * turns);
            var pts = new StringBuilder();
            for (var s = 0; s <= steps; s++)
            {
                var t = 2 * Math.PI * turns * s / steps;
                var x = (R - r) * Math.Cos(t) + d * Math.Cos((R - r) / r * t);
                var y = (R - r) * Math.Sin(t) - d * Math.Sin((R - r) / r * t);
                pts.Append(s == 0 ? 'M' : 'L').Append(F(x)).Append(' ').Append(F(y));
            }
            sb.Append($"<path class=\"engrave\" style=\"--i:{i++}\" pathLength=\"1\" opacity=\"{F(o)}\" d=\"{pts}Z\"/>");
        }
        return sb.ToString();
    }

    private static string BuildWaves()
    {
        var sb = new StringBuilder();
        for (var line = 0; line < 9; line++)
        {
            var pts = new StringBuilder();
            for (var x = 0; x <= 400; x += 4)
            {
                var y = 30 + Math.Sin(x / 22.0 + line * 0.55) * (10 + line) * 0.9 + Math.Sin(x / 57.0 - line) * 6;
                pts.Append(x == 0 ? 'M' : 'L').Append(x).Append(' ').Append(F(y));
            }
            sb.Append($"<path d=\"{pts}\"/>");
        }
        return sb.ToString();
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}

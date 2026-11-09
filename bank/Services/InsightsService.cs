using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public record BalancePoint(DateOnly Date, decimal Balance);
public record CategoryTotal(TransactionCategory Category, decimal Amount, int Count);
public record MonthFlow(int Year, int Month, decimal MoneyIn, decimal MoneyOut)
{
    public string Label => new DateOnly(Year, Month, 1).ToString("MMM", Money.Culture);
    public decimal Net => MoneyIn - MoneyOut;
}

public interface IInsightsService
{
    Task<List<BalancePoint>> BalanceHistoryAsync(string userId, int days);
    Task<List<CategoryTotal>> SpendingByCategoryAsync(string userId, int year, int month);
    Task<List<MonthFlow>> CashflowAsync(string userId, int months);
}

/// <summary>
/// Chart data. Internal transfers between a user's own accounts are excluded from income and spending
/// (they net to zero). Sums happen in memory: money is stored as cents and the queries are already
/// narrowed to one user and a date window, which keeps the row counts small.
/// </summary>
public class InsightsService(ApplicationDbContext db, TimeProvider clock) : IInsightsService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<List<BalancePoint>> BalanceHistoryAsync(string userId, int days)
    {
        days = Math.Clamp(days, 7, 365);
        var today = DateOnly.FromDateTime(Now);
        var start = today.AddDays(-(days - 1));
        var startUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var balances = await db.BankAccounts.Where(a => a.OwnerId == userId && a.IsActive).Select(a => a.Balance).ToListAsync();
        var current = balances.Sum();
        var lines = await db.Transactions
            .Where(t => t.BankAccount.OwnerId == userId && t.PostedUtc >= startUtc)
            .Select(t => new { t.PostedUtc, t.Amount }).ToListAsync();
        var byDay = lines.GroupBy(l => DateOnly.FromDateTime(l.PostedUtc)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        // Walk backwards from today's balance, undoing each day's net movement.
        var points = new List<BalancePoint>(days);
        var running = current;
        for (var d = today; d >= start; d = d.AddDays(-1))
        {
            points.Add(new BalancePoint(d, running));
            running -= byDay.GetValueOrDefault(d);
        }
        points.Reverse();
        return points;
    }

    public async Task<List<CategoryTotal>> SpendingByCategoryAsync(string userId, int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var lines = await db.Transactions
            .Where(t => t.BankAccount.OwnerId == userId && t.PostedUtc >= start && t.PostedUtc < end
                        && t.Amount < 0 && t.Type != TransactionType.TransferOut)
            .Select(t => new { t.Category, t.Amount }).ToListAsync();
        return lines.GroupBy(l => l.Category)
            .Select(g => new CategoryTotal(g.Key, -g.Sum(x => x.Amount), g.Count()))
            .OrderByDescending(c => c.Amount).ToList();
    }

    public async Task<List<MonthFlow>> CashflowAsync(string userId, int months)
    {
        months = Math.Clamp(months, 1, 24);
        var first = new DateTime(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(months - 1));
        var lines = await db.Transactions
            .Where(t => t.BankAccount.OwnerId == userId && t.PostedUtc >= first
                        && t.Type != TransactionType.TransferIn && t.Type != TransactionType.TransferOut)
            .Select(t => new { t.PostedUtc, t.Amount }).ToListAsync();

        var result = new List<MonthFlow>(months);
        for (var m = first; m <= Now; m = m.AddMonths(1))
        {
            var inMonth = lines.Where(l => l.PostedUtc.Year == m.Year && l.PostedUtc.Month == m.Month).ToList();
            result.Add(new MonthFlow(m.Year, m.Month,
                inMonth.Where(l => l.Amount > 0).Sum(l => l.Amount),
                -inMonth.Where(l => l.Amount < 0).Sum(l => l.Amount)));
        }
        return result;
    }
}

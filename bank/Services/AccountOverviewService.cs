using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

/// <summary>One account on the Accounts page, with the figures its panel shows.</summary>
/// <param name="InterestRate">Yearly rate as a fraction (0.02 is 2%); 0 for checking.</param>
/// <param name="ScheduledCount">Active scheduled payments that move money out of or into this account.</param>
/// <param name="Next">The soonest of those payments, or null when nothing is scheduled.</param>
public record AccountSummary(BankAccount Account, decimal In30, decimal Out30, decimal InterestRate, decimal InterestThisYear,
    int ScheduledCount, ScheduledPayment? Next);

public interface IAccountOverviewService
{
    Task<List<AccountSummary>> ForOwnerAsync(string userId);
}

/// <summary>
/// Builds the Accounts page in three queries for the whole list (accounts, this year's transactions, active schedules)
/// instead of a round of queries per account.
/// </summary>
public class AccountOverviewService(ApplicationDbContext db, TimeProvider clock) : IAccountOverviewService
{
    public async Task<List<AccountSummary>> ForOwnerAsync(string userId)
    {
        var accounts = await db.BankAccounts.AsNoTracking()
            .Where(a => a.OwnerId == userId)
            // Open first, primary first, then checking, savings, goals.
            .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.IsPrimary)
            .ThenBy(a => a.Type == AccountType.Checking ? 0 : a.Type == AccountType.Savings ? 1 : 2).ThenBy(a => a.Id)
            .ToListAsync();
        var ids = accounts.Select(a => a.Id).ToList();

        var now = clock.GetUtcNow().UtcDateTime;
        var since30 = now.AddDays(-30);
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var from = since30 < yearStart ? since30 : yearStart;
        // Amounts are summed here rather than in SQL: SQLite can't sum the decimal columns.
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => ids.Contains(t.BankAccountId) && t.PostedUtc >= from)
            .Select(t => new { t.BankAccountId, t.Amount, t.Type, t.PostedUtc })
            .ToListAsync();

        var schedules = await db.ScheduledPayments.AsNoTracking()
            .Include(s => s.FromAccount).Include(s => s.ToAccount).Include(s => s.Payee)
            .Where(s => s.OwnerId == userId && s.Status == ScheduleStatus.Active)
            .OrderBy(s => s.NextRunDate).ThenBy(s => s.Id)
            .ToListAsync();

        return accounts.Select(a =>
        {
            var mine = recent.Where(t => t.BankAccountId == a.Id).ToList();
            var last30 = mine.Where(t => t.PostedUtc >= since30).ToList();
            var touching = schedules.Where(s => s.FromAccountId == a.Id || s.ToAccountId == a.Id).ToList();
            return new AccountSummary(a,
                In30: last30.Where(t => t.Amount > 0).Sum(t => t.Amount),
                Out30: -last30.Where(t => t.Amount < 0).Sum(t => t.Amount),
                InterestRate: InterestService.RateFor(a.Type),
                InterestThisYear: mine.Where(t => t.Type == TransactionType.Interest && t.PostedUtc >= yearStart).Sum(t => t.Amount),
                ScheduledCount: touching.Count,
                Next: touching.FirstOrDefault());
        }).ToList();
    }
}

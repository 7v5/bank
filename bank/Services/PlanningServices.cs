using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

// ---------- Budgets ----------

public record BudgetStatus(int Id, TransactionCategory Category, decimal Limit, decimal Spent)
{
    public decimal Left => Limit - Spent;
    public bool IsOver => Spent > Limit;
    public bool IsNear => !IsOver && Spent >= Limit * BudgetService.AlertAt;
    /// <summary>0 to 100, for the width of the bar.</summary>
    public int Percent => Limit <= 0 ? 0 : (int)Math.Min(100, Math.Round(Spent / Limit * 100));
}

public interface IBudgetService
{
    Task<List<BudgetStatus>> StatusAsync(string userId);
    Task SetAsync(string userId, TransactionCategory category, decimal monthlyLimit);
    Task RemoveAsync(string userId, int id);
    Task<int> SendAlertsAsync(CancellationToken ct = default);
}

/// <summary>Monthly spending limits per category, measured against this calendar month's spending.</summary>
public class BudgetService(ApplicationDbContext db, IInsightsService insights, INotificationService notifications, TimeProvider clock) : IBudgetService
{
    public const decimal AlertAt = 0.9m;
    public const decimal MaxLimit = 100_000m;

    /// <summary>Categories money is spent in. Income and transfers between your own accounts can't be budgeted.</summary>
    public static readonly TransactionCategory[] Categories =
    [
        TransactionCategory.Groceries, TransactionCategory.Dining, TransactionCategory.Bills, TransactionCategory.Shopping,
        TransactionCategory.Transport, TransactionCategory.Entertainment, TransactionCategory.People, TransactionCategory.Other
    ];

    public async Task<List<BudgetStatus>> StatusAsync(string userId)
    {
        var budgets = await db.Budgets.AsNoTracking().Where(b => b.OwnerId == userId).ToListAsync();
        if (budgets.Count == 0) return [];
        var now = clock.GetUtcNow().UtcDateTime;
        var spent = (await insights.SpendingByCategoryAsync(userId, now.Year, now.Month)).ToDictionary(c => c.Category, c => c.Amount);
        return budgets.Select(b => new BudgetStatus(b.Id, b.Category, b.MonthlyLimit, spent.GetValueOrDefault(b.Category)))
            .OrderBy(b => Array.IndexOf(Categories, b.Category)).ToList();
    }

    public async Task SetAsync(string userId, TransactionCategory category, decimal monthlyLimit)
    {
        if (!Categories.Contains(category)) throw new BankingException("That category can't have a budget.");
        if (monthlyLimit <= 0) throw new BankingException("Enter a limit greater than zero.");
        if (decimal.Round(monthlyLimit, 2) != monthlyLimit) throw new BankingException("Amounts can have at most two decimal places.");
        if (monthlyLimit > MaxLimit) throw new BankingException($"The highest limit is {Money.Format(MaxLimit)}.");

        var budget = await db.Budgets.SingleOrDefaultAsync(b => b.OwnerId == userId && b.Category == category);
        if (budget is null) db.Budgets.Add(new Budget { OwnerId = userId, Category = category, MonthlyLimit = monthlyLimit });
        else
        {
            budget.MonthlyLimit = monthlyLimit;
            budget.AlertedMonth = 0; // a new limit can alert again
        }
        await db.SaveChangesAsync();
    }

    public Task RemoveAsync(string userId, int id) => db.Budgets.Where(b => b.Id == id && b.OwnerId == userId).ExecuteDeleteAsync();

    /// <summary>Sends one notification per budget per month when spending reaches 90% of the limit.</summary>
    public async Task<int> SendAlertsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var stamp = now.Year * 100 + now.Month;
        var owners = await db.Budgets.Where(b => b.AlertedMonth != stamp).Select(b => b.OwnerId).Distinct().ToListAsync(ct);
        var sent = 0;
        foreach (var owner in owners)
        {
            var statuses = (await StatusAsync(owner)).Where(s => s.IsNear || s.IsOver).ToDictionary(s => s.Id);
            if (statuses.Count == 0) continue;
            foreach (var b in await db.Budgets.Where(b => b.OwnerId == owner && b.AlertedMonth != stamp).ToListAsync(ct))
            {
                if (!statuses.TryGetValue(b.Id, out var s)) continue;
                b.AlertedMonth = stamp;
                notifications.Add(owner, s.IsOver ? $"You're over your {b.Category} budget" : $"Your {b.Category} budget is nearly used up",
                    $"{Money.Format(s.Spent)} spent of {Money.Format(s.Limit)} this month.", "chart", "/Budgets");
                sent++;
            }
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }
}

// ---------- Statements ----------

public record Statement(BankAccount Account, int Year, int Month, decimal Opening, decimal Closing, decimal MoneyIn, decimal MoneyOut, List<Transaction> Lines)
{
    public DateOnly From => new(Year, Month, 1);
    public DateOnly To => From.AddMonths(1).AddDays(-1);
}

public interface IStatementService
{
    Task<Statement?> BuildAsync(string userId, int accountId, int year, int month);
    List<DateOnly> MonthsFor(BankAccount account);
}

/// <summary>One account's activity for one calendar month, with the balance it started and ended on.</summary>
public class StatementService(ApplicationDbContext db, TimeProvider clock) : IStatementService
{
    public async Task<Statement?> BuildAsync(string userId, int accountId, int year, int month)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12) return null;
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.OwnerId == userId);
        if (account is null) return null;

        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var lines = await db.Transactions.AsNoTracking()
            .Where(t => t.BankAccountId == accountId && t.PostedUtc >= start && t.PostedUtc < end)
            .OrderBy(t => t.PostedUtc).ThenBy(t => t.Id).ToListAsync();
        // The balance before the month began is whatever the last earlier line left behind.
        var opening = await db.Transactions.AsNoTracking()
            .Where(t => t.BankAccountId == accountId && t.PostedUtc < start)
            .OrderByDescending(t => t.PostedUtc).ThenByDescending(t => t.Id)
            .Select(t => (decimal?)t.BalanceAfter).FirstOrDefaultAsync() ?? 0m;
        var closing = lines.Count > 0 ? lines[^1].BalanceAfter : opening;
        return new Statement(account, year, month, opening, closing,
            lines.Where(l => l.Amount > 0).Sum(l => l.Amount), -lines.Where(l => l.Amount < 0).Sum(l => l.Amount), lines);
    }

    /// <summary>Months a statement exists for, newest first: from the month the account opened to now (at most 24).</summary>
    public List<DateOnly> MonthsFor(BankAccount account)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var last = account.ClosedUtc is { } closed && closed < now ? closed : now;
        var month = new DateOnly(last.Year, last.Month, 1);
        var first = new DateOnly(account.OpenedUtc.Year, account.OpenedUtc.Month, 1);
        var months = new List<DateOnly>();
        for (; month >= first && months.Count < 24; month = month.AddMonths(-1)) months.Add(month);
        return months;
    }
}

// ---------- Interest ----------

public interface IInterestService
{
    Task<int> PostDueAsync(CancellationToken ct = default);
    Task<decimal> PaidThisYearAsync(int accountId);
}

/// <summary>
/// Pays interest on savings and goal accounts once a month, for the month that just ended.
/// The ledger reference is fixed per account and month, so interest for a month is never paid twice.
/// </summary>
public class InterestService(ApplicationDbContext db, LedgerPoster poster, INotificationService notifications, TimeProvider clock) : IInterestService
{
    /// <summary>Yearly rate as a fraction: 0.02 is 2%.</summary>
    public static decimal RateFor(AccountType type) => type switch
    {
        AccountType.Savings => 0.02m,
        AccountType.Goal => 0.025m,
        _ => 0m
    };

    /// <summary>One month of interest on a balance, rounded to the cent.</summary>
    public static decimal MonthlyInterest(decimal balance, decimal yearlyRate) =>
        balance <= 0 || yearlyRate <= 0 ? 0m : decimal.Round(balance * yearlyRate / 12m, 2, MidpointRounding.ToEven);

    public static string Reference(int accountId, int year, int month) => $"INT-{accountId}-{year}{month:00}";

    public async Task<int> PostDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var earned = monthStart.AddMonths(-1); // the month the interest is for
        var accounts = await db.BankAccounts
            .Where(a => a.IsActive && a.Type != AccountType.Checking && a.Balance > 0 && a.OpenedUtc < monthStart)
            .ToListAsync(ct);
        var posted = 0;
        foreach (var account in accounts)
        {
            var reference = Reference(account.Id, earned.Year, earned.Month);
            var amount = MonthlyInterest(account.Balance, RateFor(account.Type));
            if (amount <= 0 || await db.Transactions.AnyAsync(t => t.Reference == reference, ct)) continue;
            var label = earned.ToString("MMMM", Money.Culture);
            poster.Post(account, amount, TransactionType.Interest, TransactionCategory.Income, $"Interest for {label}", reference, "SimpleBank", now);
            notifications.Add(account.OwnerId, $"{Money.Format(amount)} interest paid", $"{account.Name} earned interest for {label}.",
                "arrow-down", $"/Accounts/Details/{account.Id}");
            posted++;
        }
        if (posted > 0) await db.SaveChangesAsync(ct);
        return posted;
    }

    public async Task<decimal> PaidThisYearAsync(int accountId)
    {
        var year = new DateTime(clock.GetUtcNow().Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var amounts = await db.Transactions.Where(t => t.BankAccountId == accountId && t.Type == TransactionType.Interest && t.PostedUtc >= year)
            .Select(t => t.Amount).ToListAsync();
        return amounts.Sum();
    }
}

// ---------- Background runner ----------

/// <summary>Once a minute: makes scheduled payments that are due, pays monthly interest and sends budget alerts.</summary>
public class BankClockRunner(IServiceScopeFactory scopes, ILogger<BankClockRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                var paid = await sp.GetRequiredService<IScheduledPaymentService>().RunDueAsync(stoppingToken);
                var interest = await sp.GetRequiredService<IInterestService>().PostDueAsync(stoppingToken);
                var alerts = await sp.GetRequiredService<IBudgetService>().SendAlertsAsync(stoppingToken);
                if (paid + interest + alerts > 0)
                    logger.LogInformation("Bank clock: {Paid} scheduled payments, {Interest} interest postings, {Alerts} budget alerts", paid, interest, alerts);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bank clock run failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}

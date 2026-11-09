using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public record AdminStats(int Customers, int NewCustomers30d, int OpenAccounts, decimal TotalDeposits, decimal Volume30d, int Transactions30d,
    int PendingRequests, int FrozenAccounts, List<(DateOnly Day, decimal Volume, int Count)> DailyVolume, List<(AccountType Type, decimal Balance, int Count)> ByType);

public record CustomerRow(ApplicationUser User, int Accounts, decimal Balance, bool IsLocked, DateTime? LastActivity);

public interface IAdminService
{
    Task<AdminStats> StatsAsync();
    Task<PagedResult<CustomerRow>> CustomersAsync(string? q, int page, int pageSize = 15);
    Task SetFrozenAsync(string adminId, int accountId, bool frozen);
    Task SetLockedAsync(string adminId, string userId, bool locked);
    Task<PostingResult> AdjustAsync(string adminId, int accountId, decimal signedAmount, string reason);
}

/// <summary>Bank-staff operations. Every change is audited.</summary>
public class AdminService(ApplicationDbContext db, UserManager<ApplicationUser> users, LedgerPoster poster,
    INotificationService notifications, IAuditService audit, TimeProvider clock) : IAdminService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IQueryable<ApplicationUser> Customers =>
        db.Users.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == DbInitializer.CustomerRole)));

    public async Task<AdminStats> StatsAsync()
    {
        var since = Now.Date.AddDays(-29);
        var customers = await Customers.CountAsync();
        var newCustomers = await Customers.CountAsync(u => u.CreatedUtc >= since);
        var accounts = await db.BankAccounts.Where(a => a.IsActive).Select(a => new { a.Type, a.Balance, a.IsFrozen }).ToListAsync();
        var recent = await db.Transactions.Where(t => t.PostedUtc >= since).Select(t => new { t.PostedUtc, t.Amount, t.Type }).ToListAsync();
        var pending = await db.MoneyRequests.CountAsync(r => r.Status == MoneyRequestStatus.Pending);

        // Volume counts money leaving accounts (each movement once, not both legs).
        var outflows = recent.Where(t => t.Amount < 0).ToList();
        var daily = Enumerable.Range(0, 30).Select(i => DateOnly.FromDateTime(since.AddDays(i))).Select(d =>
        {
            var day = outflows.Where(t => DateOnly.FromDateTime(t.PostedUtc) == d).ToList();
            return (d, -day.Sum(t => t.Amount), day.Count);
        }).ToList();

        return new AdminStats(customers, newCustomers, accounts.Count, accounts.Sum(a => a.Balance), -outflows.Sum(t => t.Amount), recent.Count, pending,
            accounts.Count(a => a.IsFrozen), daily,
            accounts.GroupBy(a => a.Type).Select(g => (g.Key, g.Sum(a => a.Balance), g.Count())).OrderBy(x => x.Key).ToList());
    }

    public async Task<PagedResult<CustomerRow>> CustomersAsync(string? q, int page, int pageSize = 15)
    {
        var query = Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().TrimStart('@') + "%";
            query = query.Where(u => EF.Functions.Like(u.FullName, pattern) || EF.Functions.Like(u.Email!, pattern) || EF.Functions.Like(u.Handle, pattern));
        }
        var total = await query.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var list = await query.OrderBy(u => u.FullName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var ids = list.Select(u => u.Id).ToList();
        var accts = await db.BankAccounts.Where(a => ids.Contains(a.OwnerId) && a.IsActive).Select(a => new { a.OwnerId, a.Balance }).ToListAsync();
        var last = await db.Transactions.Where(t => ids.Contains(t.BankAccount.OwnerId))
            .GroupBy(t => t.BankAccount.OwnerId).Select(g => new { g.Key, When = g.Max(t => t.PostedUtc) }).ToDictionaryAsync(x => x.Key, x => x.When);
        var rows = list.Select(u => new CustomerRow(u, accts.Count(a => a.OwnerId == u.Id), accts.Where(a => a.OwnerId == u.Id).Sum(a => a.Balance),
            u.LockoutEnd > DateTimeOffset.UtcNow, last.TryGetValue(u.Id, out var w) ? w : null)).ToList();
        return new PagedResult<CustomerRow>(rows, page, pageSize, total);
    }

    public async Task SetFrozenAsync(string adminId, int accountId, bool frozen)
    {
        var account = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == accountId) ?? throw new BankingException("Account was not found.");
        if (account.IsFrozen == frozen) return;
        account.IsFrozen = frozen;
        notifications.Add(account.OwnerId, frozen ? $"{account.Name} has been frozen" : $"{account.Name} is active again",
            frozen ? "Outgoing payments from this account are paused. Contact support for help." : "You can send and withdraw from this account again.",
            "snowflake", $"/Accounts/Details/{account.Id}");
        await db.SaveChangesAsync();
        await audit.LogAsync(adminId, frozen ? "account.freeze" : "account.unfreeze", "Account", account.Id.ToString(), $"{account.Name} ({account.MaskedNumber})");
    }

    public async Task SetLockedAsync(string adminId, string userId, bool locked)
    {
        if (userId == adminId) throw new BankingException("You can't lock your own account.");
        var user = await users.FindByIdAsync(userId) ?? throw new BankingException("Customer was not found.");
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        if (!locked) await users.ResetAccessFailedCountAsync(user);
        // Changing the stamp signs the customer out everywhere on their next request.
        await users.UpdateSecurityStampAsync(user);
        await audit.LogAsync(adminId, locked ? "user.lock" : "user.unlock", "User", user.Id, $"{user.FullName} ({user.Email})");
    }

    public async Task<PostingResult> AdjustAsync(string adminId, int accountId, decimal signedAmount, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new BankingException("Give a reason of at least 5 characters.");
        if (signedAmount == 0 || decimal.Round(signedAmount, 2) != signedAmount) throw new BankingException("Enter a non-zero amount with at most two decimals.");
        if (Math.Abs(signedAmount) > 100_000m) throw new BankingException("Adjustments are limited to $100,000.");
        var result = await db.InTransactionAsync(async () =>
        {
            var account = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive) ?? throw new BankingException("Account was not found.");
            var reference = LedgerPoster.NewReference("ADJ");
            poster.Post(account, signedAmount, TransactionType.AdminAdjustment, signedAmount > 0 ? TransactionCategory.Income : TransactionCategory.Other,
                $"Bank adjustment: {reason.Trim()}", reference, "SimpleBank", Now);
            notifications.Add(account.OwnerId, $"{Money.Signed(signedAmount)} adjustment on {account.Name}", reason.Trim(), "shield", $"/Accounts/Details/{account.Id}");
            return new PostingResult(reference, account.Balance);
        });
        await audit.LogAsync(adminId, "account.adjust", "Account", accountId.ToString(), $"{Money.Signed(signedAmount)}: {reason.Trim()} ({result.Reference})");
        return result;
    }
}

using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public record PostingResult(string Reference, decimal NewBalance);

public interface IBankingService
{
    Task<PostingResult> DepositAsync(string userId, int accountId, decimal amount, string? memo, Guid? idempotencyKey = null);
    Task<PostingResult> WithdrawAsync(string userId, int accountId, decimal amount, string? memo, Guid? idempotencyKey = null);
    Task<PostingResult> TransferAsync(string userId, int fromId, int toId, decimal amount, string? memo, Guid? idempotencyKey = null);
    Task<PostingResult> SendToUserAsync(string senderId, int fromAccountId, string recipient, decimal amount, string? note, Guid? idempotencyKey = null);
    Task<PostingResult> PayBillAsync(string userId, int fromId, int payeeId, decimal amount, string? memo, Guid? idempotencyKey = null);

    Task<BankAccount> OpenAccountAsync(string userId, string name, AccountType type, decimal? goalAmount = null);
    Task CloseAccountAsync(string userId, int accountId);
    Task SetPrimaryAsync(string userId, int accountId);
    Task RenameAsync(string userId, int accountId, string name);

    Task<ApplicationUser?> FindRecipientAsync(string query);
    Task<decimal> SentInLast24HoursAsync(string userId);
}

public class BankingService(ApplicationDbContext db, LedgerPoster poster, INotificationService notifications, TimeProvider clock) : IBankingService
{
    public const decimal MaxCashAmount = 10_000m;      // per deposit / withdrawal
    public const decimal MaxTransferAmount = 1_000_000m;
    public const decimal DailySendLimit = 5_000m;      // rolling 24h, person-to-person
    public const int MaxOpenAccounts = 8;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---------- Cash ----------

    public Task<PostingResult> DepositAsync(string userId, int accountId, decimal amount, string? memo, Guid? idempotencyKey = null)
    {
        ValidateAmount(amount, MaxCashAmount);
        return db.InTransactionAsync(async () =>
        {
            await ClaimKeyAsync(userId, idempotencyKey);
            var account = await LoadOwnedAsync(userId, accountId);
            var reference = LedgerPoster.NewReference("DEP");
            poster.Post(account, amount, TransactionType.Deposit, TransactionCategory.Cash,
                Clean(memo) ?? "Mobile deposit", reference, postedUtc: Now);
            return new PostingResult(reference, account.Balance);
        });
    }

    public Task<PostingResult> WithdrawAsync(string userId, int accountId, decimal amount, string? memo, Guid? idempotencyKey = null)
    {
        ValidateAmount(amount, MaxCashAmount);
        return db.InTransactionAsync(async () =>
        {
            await ClaimKeyAsync(userId, idempotencyKey);
            var account = await LoadOwnedAsync(userId, accountId);
            EnsureCanSend(account, amount);
            var reference = LedgerPoster.NewReference("WDL");
            poster.Post(account, -amount, TransactionType.Withdrawal, TransactionCategory.Cash,
                Clean(memo) ?? "Cash withdrawal", reference, postedUtc: Now);
            return new PostingResult(reference, account.Balance);
        });
    }

    // ---------- Moving money ----------

    public Task<PostingResult> TransferAsync(string userId, int fromId, int toId, decimal amount, string? memo, Guid? idempotencyKey = null)
    {
        ValidateAmount(amount, MaxTransferAmount);
        if (fromId == toId) throw new BankingException("Choose two different accounts.");
        return db.InTransactionAsync(async () =>
        {
            await ClaimKeyAsync(userId, idempotencyKey);
            var from = await LoadOwnedAsync(userId, fromId);
            var to = await LoadOwnedAsync(userId, toId);
            EnsureCanSend(from, amount);
            var reference = LedgerPoster.NewReference("TRF");
            var text = Clean(memo);
            poster.Post(from, -amount, TransactionType.TransferOut, TransactionCategory.Transfer, text ?? $"Transfer to {to.Name}", reference, to.Name, Now);
            poster.Post(to, amount, TransactionType.TransferIn, TransactionCategory.Transfer, text ?? $"Transfer from {from.Name}", reference, from.Name, Now);
            return new PostingResult(reference, from.Balance);
        });
    }

    public async Task<PostingResult> SendToUserAsync(string senderId, int fromAccountId, string recipient, decimal amount, string? note, Guid? idempotencyKey = null)
    {
        ValidateAmount(amount, DailySendLimit);
        var target = await FindRecipientAsync(recipient) ?? throw new BankingException("We couldn't find anyone with that handle or email.");
        return await db.InTransactionAsync(async () =>
        {
            await ClaimKeyAsync(senderId, idempotencyKey);
            return await SendCoreAsync(senderId, fromAccountId, target, amount, note);
        });
    }

    /// <summary>
    /// Person-to-person posting without its own transaction, so MoneyRequestService can pay a request
    /// and update the request row inside one atomic unit of work.
    /// </summary>
    internal async Task<PostingResult> SendCoreAsync(string senderId, int fromAccountId, ApplicationUser recipient, decimal amount, string? note)
    {
        if (recipient.Id == senderId) throw new BankingException("You can't send money to yourself. Use a transfer instead.");
        var from = await LoadOwnedAsync(senderId, fromAccountId);
        EnsureCanSend(from, amount);

        var sentToday = await SentInLast24HoursAsync(senderId);
        if (sentToday + amount > DailySendLimit)
            throw new BankingException($"This would exceed your daily sending limit of {Money.Format(DailySendLimit)}. You can send up to {Money.Format(Math.Max(0, DailySendLimit - sentToday))} more today.");

        var to = await db.BankAccounts
            .Where(a => a.OwnerId == recipient.Id && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id)
            .FirstOrDefaultAsync() ?? throw new BankingException($"{recipient.FullName} can't receive payments right now.");
        var sender = await db.Users.SingleAsync(u => u.Id == senderId);

        var reference = LedgerPoster.NewReference("P2P");
        var text = Clean(note);
        poster.Post(from, -amount, TransactionType.P2PSent, TransactionCategory.People, text ?? $"Sent to {recipient.FullName}", reference, recipient.FullName, Now);
        poster.Post(to, amount, TransactionType.P2PReceived, TransactionCategory.People, text ?? $"From {sender.FullName}", reference, sender.FullName, Now);

        notifications.Add(recipient.Id, $"{sender.FullName} sent you {Money.Format(amount)}",
            text is null ? $"Deposited into {to.Name}." : $"“{text}” — deposited into {to.Name}.",
            "arrow-down", $"/Accounts/Details/{to.Id}");
        return new PostingResult(reference, from.Balance);
    }

    public Task<PostingResult> PayBillAsync(string userId, int fromId, int payeeId, decimal amount, string? memo, Guid? idempotencyKey = null)
    {
        ValidateAmount(amount, MaxTransferAmount);
        return db.InTransactionAsync(async () =>
        {
            await ClaimKeyAsync(userId, idempotencyKey);
            var account = await LoadOwnedAsync(userId, fromId);
            var payee = await db.Payees.SingleOrDefaultAsync(p => p.Id == payeeId && p.OwnerId == userId)
                ?? throw new BankingException("Payee was not found.");
            EnsureCanSend(account, amount);
            var reference = LedgerPoster.NewReference("BIL");
            poster.Post(account, -amount, TransactionType.BillPayment, payee.Category,
                Clean(memo) ?? $"Payment to {payee.Name}", reference, payee.Name, Now);
            return new PostingResult(reference, account.Balance);
        });
    }

    // ---------- Account management ----------

    public async Task<BankAccount> OpenAccountAsync(string userId, string name, AccountType type, decimal? goalAmount = null)
    {
        var clean = Clean(name) ?? throw new BankingException("Give the account a name.");
        var open = await db.BankAccounts.CountAsync(a => a.OwnerId == userId && a.IsActive);
        if (open >= MaxOpenAccounts) throw new BankingException($"You can have at most {MaxOpenAccounts} open accounts.");

        var account = new BankAccount
        {
            OwnerId = userId, Name = clean, Type = type, IsPrimary = open == 0,
            AccountNumber = await NewAccountNumberAsync(db, Random.Shared), OpenedUtc = Now
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public Task CloseAccountAsync(string userId, int accountId) => db.InTransactionAsync(async () =>
    {
        var account = await LoadOwnedAsync(userId, accountId);
        if (account.IsFrozen) throw new BankingException("Frozen accounts can't be closed. Contact support.");
        if (account.Balance != 0) throw new BankingException($"Move the remaining {Money.Format(account.Balance)} out before closing this account.");
        var others = await db.BankAccounts.Where(a => a.OwnerId == userId && a.IsActive && a.Id != accountId)
            .OrderBy(a => a.Id).ToListAsync();
        if (others.Count == 0) throw new BankingException("You need at least one open account.");

        account.IsActive = false;
        account.ClosedUtc = Now;
        if (account.IsPrimary)
        {
            account.IsPrimary = false;
            (others.FirstOrDefault(a => a.Type == AccountType.Checking) ?? others[0]).IsPrimary = true;
        }
        return true;
    });

    public Task SetPrimaryAsync(string userId, int accountId) => db.InTransactionAsync(async () =>
    {
        var target = await LoadOwnedAsync(userId, accountId);
        var all = await db.BankAccounts.Where(a => a.OwnerId == userId && a.IsActive).ToListAsync();
        foreach (var a in all) a.IsPrimary = a.Id == target.Id;
        return true;
    });

    public async Task RenameAsync(string userId, int accountId, string name)
    {
        var account = await LoadOwnedAsync(userId, accountId);
        account.Name = Clean(name) ?? throw new BankingException("Give the account a name.");
        await db.SaveChangesAsync();
    }

    // ---------- Lookups ----------

    public async Task<ApplicationUser?> FindRecipientAsync(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        if (q.Contains('@') && q.Contains('.') && !q.StartsWith('@'))
        {
            var normalized = q.ToUpperInvariant();
            return await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized);
        }
        var handle = q.TrimStart('@').ToLowerInvariant();
        return await db.Users.SingleOrDefaultAsync(u => u.Handle == handle);
    }

    public async Task<decimal> SentInLast24HoursAsync(string userId)
    {
        var since = Now.AddHours(-24);
        var amounts = await db.Transactions
            .Where(t => t.BankAccount.OwnerId == userId && t.Type == TransactionType.P2PSent && t.PostedUtc >= since)
            .Select(t => t.Amount).ToListAsync();
        return -amounts.Sum();
    }

    // ---------- Helpers ----------

    public static async Task<string> NewAccountNumberAsync(ApplicationDbContext db, Random random)
    {
        while (true)
        {
            var number = random.NextInt64(1_000_000_000, 9_999_999_999).ToString();
            var taken = db.BankAccounts.Local.Any(a => a.AccountNumber == number)
                        || await db.BankAccounts.AnyAsync(a => a.AccountNumber == number);
            if (!taken) return number;
        }
    }

    private async Task<BankAccount> LoadOwnedAsync(string userId, int accountId) =>
        await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.OwnerId == userId && a.IsActive)
        ?? throw new BankingException("Account was not found.");

    private static void EnsureCanSend(BankAccount account, decimal amount)
    {
        if (account.IsFrozen) throw new BankingException($"{account.Name} is frozen. Outgoing payments are disabled.");
        if (account.Balance < amount) throw new BankingException($"Insufficient funds. {account.Name} has {Money.Format(account.Balance)} available.");
    }

    private static void ValidateAmount(decimal amount, decimal max)
    {
        if (amount <= 0) throw new BankingException("Amount must be greater than zero.");
        if (decimal.Round(amount, 2) != amount) throw new BankingException("Amounts can have at most two decimal places.");
        if (amount > max) throw new BankingException($"The maximum for this action is {Money.Format(max)}.");
    }

    private async Task ClaimKeyAsync(string userId, Guid? key)
    {
        if (key is null || key == Guid.Empty) return;
        if (await db.ProcessedRequests.AnyAsync(p => p.Key == key))
            throw new BankingException("This request was already submitted.");
        db.ProcessedRequests.Add(new ProcessedRequest { Key = key.Value, UserId = userId, CreatedUtc = Now });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

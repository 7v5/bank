using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

/// <summary>
/// Moves a balance and writes the matching ledger line. It never opens a database transaction
/// and never saves: callers (BankingService, MoneyRequestService, DbInitializer) own the unit of work.
/// Keeping this the single place balances change guarantees Balance always equals the latest BalanceAfter.
/// </summary>
public sealed class LedgerPoster(ApplicationDbContext db)
{
    public Transaction Post(BankAccount account, decimal signedAmount, TransactionType type, TransactionCategory category,
        string description, string reference, string? counterparty = null, DateTime? postedUtc = null)
    {
        if (signedAmount == 0) throw new BankingException("Amount must not be zero.");
        if (signedAmount < 0 && account.Balance + signedAmount < 0) throw new BankingException("Insufficient funds.");

        account.Balance += signedAmount;
        var line = new Transaction
        {
            BankAccount = account,
            BankAccountId = account.Id,
            Type = type,
            Category = category,
            Amount = signedAmount,
            BalanceAfter = account.Balance,
            Description = Truncate(description, 200),
            CounterpartyName = counterparty is null ? null : Truncate(counterparty, 100),
            Reference = reference,
            PostedUtc = postedUtc ?? DateTime.UtcNow
        };
        db.Transactions.Add(line);
        return line;
    }

    public static string NewReference(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

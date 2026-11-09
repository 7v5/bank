using System.ComponentModel.DataAnnotations;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Dtos;

// Shapes returned by /api/v1. Entities are never serialized directly.

public record AccountDto(int Id, string Name, AccountType Type, decimal Balance, string MaskedNumber,
    bool IsPrimary, bool IsFrozen, bool IsActive, decimal? GoalAmount, decimal? GoalProgress)
{
    public static AccountDto From(BankAccount a) =>
        new(a.Id, a.Name, a.Type, a.Balance, a.MaskedNumber, a.IsPrimary, a.IsFrozen, a.IsActive, a.GoalAmount, a.GoalProgress);
}

public record TransactionDto(long Id, int AccountId, string AccountName, DateTime PostedUtc, TransactionType Type,
    TransactionCategory Category, decimal Amount, decimal BalanceAfter, string Description, string? Counterparty, string Reference)
{
    public static TransactionDto From(Transaction t) =>
        new(t.Id, t.BankAccountId, t.BankAccount.Name, t.PostedUtc, t.Type, t.Category, t.Amount, t.BalanceAfter, t.Description, t.CounterpartyName, t.Reference);
}

public record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public record PostingResultDto(string Reference, decimal NewBalance);

public record UserLookupDto(string Handle, string Name, string Initials, string AvatarColor);

public record NotificationDto(int Id, string Title, string Body, string Icon, string? Link, bool IsRead, DateTime CreatedUtc, string Ago);

public record NotificationSummaryDto(int Unread, int PendingRequests, NotificationDto? Latest);

public record ListDto<T>(IReadOnlyList<T> Items);

public record BalancePointDto(DateOnly Date, decimal Balance);
public record CategoryTotalDto(TransactionCategory Category, decimal Amount, int Count);
public record MonthFlowDto(string Label, int Year, int Month, decimal MoneyIn, decimal MoneyOut, decimal Net);

public record AntiforgeryTokenDto(string HeaderName, string Token);

// ---------- Request bodies ----------

public class CashRequest
{
    [Required] public int AccountId { get; set; }
    [Required, Range(typeof(decimal), "0.01", "10000")] public decimal Amount { get; set; }
    [StringLength(140)] public string? Memo { get; set; }
    /// <summary>Optional. Send the same key again and the request is rejected instead of being applied twice.</summary>
    public Guid? IdempotencyKey { get; set; }
}

public class TransferRequest
{
    [Required] public int FromAccountId { get; set; }
    [Required] public int ToAccountId { get; set; }
    [Required, Range(typeof(decimal), "0.01", "1000000")] public decimal Amount { get; set; }
    [StringLength(140)] public string? Memo { get; set; }
    public Guid? IdempotencyKey { get; set; }
}

public class SendRequest
{
    [Required] public int FromAccountId { get; set; }
    /// <summary>The recipient's @handle or email address.</summary>
    [Required, StringLength(256)] public string Recipient { get; set; } = "";
    [Required, Range(typeof(decimal), "0.01", "5000")] public decimal Amount { get; set; }
    [StringLength(140)] public string? Note { get; set; }
    public Guid? IdempotencyKey { get; set; }
}

public class ThemeRequest
{
    [Required, RegularExpression("^(light|dark|system)$")] public string Theme { get; set; } = "system";
}

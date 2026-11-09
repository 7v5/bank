using System.ComponentModel.DataAnnotations;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.ViewModels;

public class DashboardViewModel
{
    public required ApplicationUser User { get; init; }
    public List<BankAccount> Accounts { get; init; } = [];
    public List<Transaction> Recent { get; init; } = [];
    public List<MoneyRequest> PendingIncoming { get; init; } = [];
    public decimal MonthIn { get; init; }
    public decimal MonthOut { get; init; }
    public decimal? BalanceChange30d { get; init; }
    public decimal Total => Accounts.Sum(a => a.Balance);
}

public class AccountDetailsViewModel
{
    public required BankAccount Account { get; init; }
    public required PagedResult<Transaction> History { get; init; }
    public required TransactionQuery Query { get; init; }
    public decimal In30 { get; init; }
    public decimal Out30 { get; init; }
}

public class OpenAccountViewModel
{
    [Required(ErrorMessage = "Give the account a name."), StringLength(60)]
    public string Name { get; set; } = "";

    public AccountType Type { get; set; } = AccountType.Savings;

    [Range(typeof(decimal), "1", "1000000", ErrorMessage = "Enter a target between $1 and $1,000,000.")]
    [Display(Name = "Target amount")]
    public decimal? GoalAmount { get; set; }
}

public class RenameAccountViewModel
{
    [Required, StringLength(60)] public string Name { get; set; } = "";
    [Range(typeof(decimal), "1", "1000000")] public decimal? GoalAmount { get; set; }
}

public enum MoneyKind { Deposit, Withdraw, Transfer, Send }

/// <summary>One form model for all four money flows; each flow uses the fields it needs.</summary>
public class MoneyFormViewModel
{
    public MoneyKind Kind { get; set; }

    [Display(Name = "From")] public int? FromAccountId { get; set; }
    [Display(Name = "To")] public int? ToAccountId { get; set; }

    [StringLength(256)] public string? Recipient { get; set; }

    [Required(ErrorMessage = "Enter an amount.")]
    [Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "Enter an amount greater than zero.")]
    public decimal? Amount { get; set; }

    [StringLength(140)] public string? Memo { get; set; }

    /// <summary>Generated when the form is rendered; a replayed post is rejected by the service.</summary>
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();

    // Display data
    public List<BankAccount> Accounts { get; set; } = [];
    public decimal SentToday { get; set; }
    public ApplicationUser? RecipientUser { get; set; }
}

public record ReceiptViewModel(
    string Kind, string Title, decimal Amount, string? From, string? To, string? Memo,
    string Reference, decimal? NewBalance, DateTime PostedUtc, int? AccountId);

/// <summary>Model for the shared, AJAX-swappable transaction list + pager partial.</summary>
public record TransactionResultsModel(PagedResult<Transaction> Results, TransactionQuery Query, string Action, int? RouteId = null, bool ShowAccount = false, bool ShowOwner = false);

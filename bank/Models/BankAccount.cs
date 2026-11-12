using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SimpleBankMvc.Models;

public enum AccountType { Checking, Savings, Goal }

public class BankAccount
{
    public int Id { get; set; }
    [Required, MaxLength(20)] public string AccountNumber { get; set; } = "";
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }

    /// <summary>Target amount for Goal accounts (a savings vault working toward something).</summary>
    public decimal? GoalAmount { get; set; }

    /// <summary>False once the account is closed. Closed accounts are hidden and cannot move money.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Incoming person-to-person payments land in the primary account.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Set by an administrator. Frozen accounts can receive money but cannot send it.</summary>
    public bool IsFrozen { get; set; }

    /// <summary>Set by the customer to stop money leaving the account. Unlike a freeze, they can undo it themselves.</summary>
    public bool LockedByOwner { get; set; }

    public DateTime OpenedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }

    [Required] public string OwnerId { get; set; } = "";
    public ApplicationUser Owner { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    [NotMapped] public decimal? GoalProgress => GoalAmount is > 0 ? Math.Min(1m, Balance / GoalAmount.Value) : null;
    /// <summary>True when money can't leave: frozen by the bank or locked by the customer.</summary>
    [NotMapped] public bool IsBlocked => IsFrozen || LockedByOwner;
    [NotMapped] public string MaskedNumber => AccountNumber.Length >= 4 ? $"•••• {AccountNumber[^4..]}" : AccountNumber;
}

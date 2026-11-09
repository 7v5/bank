using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SimpleBankMvc.Models;

public enum AccountType { Checking, Savings }

public class BankAccount
{
    public int Id { get; set; }
    [Required, MaxLength(20)] public string AccountNumber { get; set; } = "";
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }

    /// <summary>False once the account is closed. Closed accounts are hidden and cannot move money.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Incoming person-to-person payments land in the primary account.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Set by an administrator. Frozen accounts can receive money but cannot send it.</summary>
    public bool IsFrozen { get; set; }

    public DateTime OpenedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }

    [Required] public string OwnerId { get; set; } = "";
    public ApplicationUser Owner { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    [NotMapped] public string MaskedNumber => AccountNumber.Length >= 4 ? $"•••• {AccountNumber[^4..]}" : AccountNumber;
}

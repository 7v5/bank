using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

public enum TransactionType { Deposit, Withdrawal, TransferIn, TransferOut, BillPayment, P2PSent, P2PReceived, AdminAdjustment }

public enum TransactionCategory { Income, Groceries, Dining, Bills, Shopping, Transport, Entertainment, People, Transfer, Cash, Other }

/// <summary>One ledger line. Amount is signed: positive = money in, negative = money out.</summary>
public class Transaction
{
    public long Id { get; set; }
    public int BankAccountId { get; set; }
    public BankAccount BankAccount { get; set; } = null!;
    public DateTime PostedUtc { get; set; } = DateTime.UtcNow;
    public TransactionType Type { get; set; }
    public TransactionCategory Category { get; set; } = TransactionCategory.Other;
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    [Required, MaxLength(200)] public string Description { get; set; } = "";
    [MaxLength(100)] public string? CounterpartyName { get; set; }

    /// <summary>Shared by every ledger line that belongs to one operation (e.g. both sides of a transfer).</summary>
    [MaxLength(40)] public string Reference { get; set; } = "";
}

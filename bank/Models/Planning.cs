using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

public enum ScheduleKind { Transfer, Bill }
public enum ScheduleFrequency { Once, Weekly, Monthly }
public enum ScheduleStatus { Active, Paused, Completed, Cancelled }

/// <summary>A transfer or bill payment the customer asked the bank to make on a future date, once or on repeat.</summary>
public class ScheduledPayment
{
    public int Id { get; set; }
    [Required] public string OwnerId { get; set; } = "";
    public ApplicationUser Owner { get; set; } = null!;

    public ScheduleKind Kind { get; set; }
    public int FromAccountId { get; set; }
    public BankAccount FromAccount { get; set; } = null!;
    /// <summary>Destination account for a transfer.</summary>
    public int? ToAccountId { get; set; }
    public BankAccount? ToAccount { get; set; }
    /// <summary>Payee for a bill payment.</summary>
    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    public decimal Amount { get; set; }
    [MaxLength(140)] public string? Memo { get; set; }

    public ScheduleFrequency Frequency { get; set; }
    /// <summary>The first run date. Monthly repeats are counted from here so the 31st doesn't drift to the 28th.</summary>
    public DateOnly StartDate { get; set; }
    public DateOnly NextRunDate { get; set; }
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Active;

    public int RunCount { get; set; }
    public DateTime? LastRunUtc { get; set; }
    [MaxLength(200)] public string? LastResult { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A monthly spending limit the customer set for one category.</summary>
public class Budget
{
    public int Id { get; set; }
    [Required] public string OwnerId { get; set; } = "";
    public ApplicationUser Owner { get; set; } = null!;
    public TransactionCategory Category { get; set; }
    public decimal MonthlyLimit { get; set; }
    /// <summary>Year * 100 + month of the last "nearly used up" alert, so it is sent once a month.</summary>
    public int AlertedMonth { get; set; }
}

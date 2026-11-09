using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

public enum MoneyRequestStatus { Pending, Paid, Declined, Cancelled }

/// <summary>Requester asks Payer for money. Payer can pay or decline; requester can cancel while pending.</summary>
public class MoneyRequest
{
    public int Id { get; set; }
    [Required] public string RequesterId { get; set; } = "";
    public ApplicationUser Requester { get; set; } = null!;
    [Required] public string PayerId { get; set; } = "";
    public ApplicationUser Payer { get; set; } = null!;
    public decimal Amount { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public MoneyRequestStatus Status { get; set; } = MoneyRequestStatus.Pending;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedUtc { get; set; }
    [MaxLength(40)] public string? PaymentReference { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

/// <summary>Idempotency key for a money-moving request. A replayed form post hits the primary key and is rejected.</summary>
public class ProcessedRequest
{
    [Key] public Guid Key { get; set; }
    [Required] public string UserId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

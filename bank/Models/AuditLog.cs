using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

/// <summary>Append-only record of security-relevant and administrative actions.</summary>
public class AuditLog
{
    public long Id { get; set; }
    [MaxLength(450)] public string? ActorId { get; set; }
    [MaxLength(100)] public string? ActorName { get; set; }
    [Required, MaxLength(60)] public string Action { get; set; } = "";
    [Required, MaxLength(40)] public string TargetType { get; set; } = "";
    [MaxLength(450)] public string? TargetId { get; set; }
    [MaxLength(500)] public string Details { get; set; } = "";
    [MaxLength(64)] public string? IpAddress { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

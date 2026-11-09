using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

public class Notification
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;
    [Required, MaxLength(120)] public string Title { get; set; } = "";
    [MaxLength(300)] public string Body { get; set; } = "";

    /// <summary>Icon name from the app's SVG sprite (e.g. "arrow-down", "request").</summary>
    [MaxLength(30)] public string Icon { get; set; } = "bell";
    [MaxLength(200)] public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

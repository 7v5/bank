using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace SimpleBankMvc.Models;

public class ApplicationUser : IdentityUser
{
    [MaxLength(100)] public string FullName { get; set; } = "";

    /// <summary>Public, unique handle (stored lowercase, without the leading @) used to send money.</summary>
    [MaxLength(20)] public string Handle { get; set; } = "";

    [MaxLength(9)] public string AvatarColor { get; set; } = "#5b5bd6";
    [MaxLength(10)] public string ThemePreference { get; set; } = "system";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<BankAccount> Accounts { get; set; } = new List<BankAccount>();

    [NotMapped]
    public string Initials
    {
        get
        {
            var parts = FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
            };
        }
    }

    [NotMapped] public string FirstName => FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? FullName;
}

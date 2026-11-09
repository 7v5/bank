using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.Models;

public class Payee
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(120)] public string AccountReference { get; set; } = "";
    public TransactionCategory Category { get; set; } = TransactionCategory.Bills;
    [Required] public string OwnerId { get; set; } = "";
    public ApplicationUser Owner { get; set; } = null!;
}

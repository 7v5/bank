using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<MoneyRequest> MoneyRequests => Set<MoneyRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<decimal>().HaveConversion<MoneyToCentsConverter>();
        c.Properties<decimal?>().HaveConversion<NullableMoneyToCentsConverter>();
        // Enums are stored as readable strings ("TransferOut") rather than numbers.
        c.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(20);
        c.Properties<TransactionType>().HaveConversion<string>().HaveMaxLength(20);
        c.Properties<TransactionCategory>().HaveConversion<string>().HaveMaxLength(20);
        c.Properties<MoneyRequestStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>().HasIndex(u => u.Handle).IsUnique();

        b.Entity<BankAccount>(e =>
        {
            e.HasIndex(x => x.AccountNumber).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.IsActive });
            e.HasOne(x => x.Owner).WithMany(x => x.Accounts).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Transaction>(e =>
        {
            e.HasIndex(x => new { x.BankAccountId, x.PostedUtc });
            e.HasIndex(x => x.Reference);
            e.HasOne(x => x.BankAccount).WithMany(x => x.Transactions).HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Payee>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MoneyRequest>(e =>
        {
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Payer).WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PayerId, x.Status });
            e.HasIndex(x => new { x.RequesterId, x.Status });
        });

        b.Entity<Notification>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedUtc });
        });
    }
}

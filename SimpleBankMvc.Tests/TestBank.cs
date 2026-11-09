using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>
/// A real SQLite database in memory, built by running the app's EF Core migrations,
/// so tests exercise the same schema, value converters and SQL translation as the app.
/// </summary>
public sealed class TestBank : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
    public ApplicationDbContext Db { get; }
    public BankingService Banking { get; }
    public MoneyRequestService Requests { get; }
    public NotificationService Notifications { get; }

    public TestBank()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        Db = new ApplicationDbContext(_options);
        Db.Database.Migrate();

        Notifications = new NotificationService(Db, Clock);
        Banking = new BankingService(Db, new LedgerPoster(Db), Notifications, Clock);
        Requests = new MoneyRequestService(Db, Banking, Notifications, Clock);
    }

    /// <summary>A fresh context that sees only what was committed to the database.</summary>
    public ApplicationDbContext Fresh() => new(_options);

    public ApplicationUser AddUser(string handle, string? name = null)
    {
        var user = new ApplicationUser
        {
            UserName = $"{handle}@test.local", NormalizedUserName = $"{handle}@test.local".ToUpperInvariant(),
            Email = $"{handle}@test.local", NormalizedEmail = $"{handle}@test.local".ToUpperInvariant(),
            Handle = handle, FullName = name ?? char.ToUpperInvariant(handle[0]) + handle[1..] + " Tester"
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    public BankAccount AddAccount(ApplicationUser owner, decimal openingBalance, string name = "Checking",
        AccountType type = AccountType.Checking, bool primary = false, bool frozen = false)
    {
        var account = new BankAccount
        {
            OwnerId = owner.Id, Name = name, Type = type, IsPrimary = primary,
            AccountNumber = BankingService.NewAccountNumberAsync(Db, Random.Shared).GetAwaiter().GetResult()
        };
        Db.BankAccounts.Add(account);
        Db.SaveChanges();
        if (openingBalance > 0)
            new LedgerPoster(Db).Post(account, openingBalance, TransactionType.Deposit, TransactionCategory.Cash,
                "Opening balance", "SEED-" + account.Id, postedUtc: Clock.Now.UtcDateTime.AddDays(-30));
        account.IsFrozen = frozen;
        Db.SaveChanges();
        return account;
    }

    public decimal BalanceOf(int accountId)
    {
        using var db = Fresh();
        return db.BankAccounts.Single(a => a.Id == accountId).Balance;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

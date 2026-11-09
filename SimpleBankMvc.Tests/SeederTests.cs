using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Tests;

public class SeederTests
{
    [Fact]
    public async Task Seeded_balances_reconcile_with_the_ledger()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection))
            .AddSingleton(TimeProvider.System);
        services.AddIdentity<ApplicationUser, IdentityRole>(o => o.Password.RequireNonAlphanumeric = false)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        await using var provider = services.BuildServiceProvider();

        await DbInitializer.InitializeAsync(provider);
        await DbInitializer.InitializeAsync(provider); // second run must be a no-op

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(6, await db.Users.CountAsync());
        var accounts = await db.BankAccounts.Include(a => a.Transactions).ToListAsync();
        Assert.NotEmpty(accounts);
        Assert.True(await db.Transactions.CountAsync() > 500, "expected months of history");

        foreach (var account in accounts)
        {
            var lines = account.Transactions.OrderBy(t => t.PostedUtc).ThenBy(t => t.Id).ToList();
            Assert.Equal(account.Balance, lines.Sum(t => t.Amount));
            Assert.True(account.Balance >= 0);
            var running = 0m;
            foreach (var line in lines)
            {
                running += line.Amount;
                Assert.Equal(running, line.BalanceAfter);
            }
        }

        // Every transfer and P2P payment nets to zero across its two ledger lines.
        var paired = await db.Transactions
            .Where(t => t.Type == TransactionType.TransferIn || t.Type == TransactionType.TransferOut
                        || t.Type == TransactionType.P2PSent || t.Type == TransactionType.P2PReceived)
            .Select(t => new { t.Reference, t.Amount }).ToListAsync();
        Assert.All(paired.GroupBy(p => p.Reference), g => Assert.Equal(0m, g.Sum(x => x.Amount)));

        // Each customer has exactly one primary account.
        var customers = await db.Users.Where(u => u.Handle != "admin").Select(u => u.Id).ToListAsync();
        Assert.All(customers, id => Assert.Single(accounts, a => a.OwnerId == id && a.IsPrimary));
    }
}

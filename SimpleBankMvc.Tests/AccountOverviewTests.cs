using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Tests;

public class AccountOverviewTests
{
    [Fact]
    public async Task Each_account_gets_its_own_30_day_totals_interest_and_next_payment()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("ola");
        var checking = bank.AddAccount(u, 500m, primary: true);           // opening deposit 30 days ago counts
        var savings = bank.AddAccount(u, 1000m, "Savings", AccountType.Savings);
        var stranger = bank.AddUser("pat");
        bank.AddAccount(stranger, 999m);

        var poster = new LedgerPoster(bank.Db);
        var now = bank.Clock.Now.UtcDateTime;
        poster.Post(checking, 7m, TransactionType.Deposit, TransactionCategory.Cash, "Too old", "OLD-1", postedUtc: now.AddDays(-40));
        poster.Post(savings, 3.5m, TransactionType.Interest, TransactionCategory.Income, "Interest", "INT-A", "SimpleBank", now.AddDays(-10));
        poster.Post(savings, 2m, TransactionType.Interest, TransactionCategory.Income, "Last year", "INT-B", "SimpleBank", new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        await bank.Db.SaveChangesAsync();
        await bank.Banking.DepositAsync(u.Id, checking.Id, 50m, null);
        await bank.Banking.WithdrawAsync(u.Id, checking.Id, 20m, null);
        await bank.Banking.TransferAsync(u.Id, checking.Id, savings.Id, 100m, null);

        var payee = new Payee { OwnerId = u.Id, Name = "City Power", AccountReference = "CP-1", Category = TransactionCategory.Bills };
        bank.Db.Payees.Add(payee);
        await bank.Db.SaveChangesAsync();
        var schedules = new ScheduledPaymentService(bank.Db, bank.Banking, bank.Notifications, bank.Clock);
        var today = DateOnly.FromDateTime(bank.Clock.GetLocalNow().DateTime);
        await schedules.CreateAsync(u.Id, ScheduleKind.Transfer, checking.Id, savings.Id, null, 25m, null, today.AddDays(5), ScheduleFrequency.Monthly);
        await schedules.CreateAsync(u.Id, ScheduleKind.Bill, checking.Id, null, payee.Id, 60m, null, today.AddDays(2), ScheduleFrequency.Monthly);
        var cancelled = await schedules.CreateAsync(u.Id, ScheduleKind.Bill, checking.Id, null, payee.Id, 9m, null, today.AddDays(1), ScheduleFrequency.Once);
        await schedules.CancelAsync(u.Id, cancelled.Id);

        var list = await new AccountOverviewService(bank.Fresh(), bank.Clock).ForOwnerAsync(u.Id);

        Assert.Equal(new[] { checking.Id, savings.Id }, list.Select(s => s.Account.Id));
        var c = list[0];
        Assert.Equal(550m, c.In30);
        Assert.Equal(120m, c.Out30);
        Assert.Equal(0m, c.InterestRate);
        Assert.Equal(2, c.ScheduledCount);
        Assert.Equal(60m, c.Next?.Amount);                               // the bill comes first; the cancelled one is ignored
        Assert.Equal("City Power", c.Next?.Payee?.Name);

        var s = list[1];
        Assert.Equal(1103.5m, s.In30);                                   // opening, transfer in, interest
        Assert.Equal(0m, s.Out30);
        Assert.Equal(0.02m, s.InterestRate);
        Assert.Equal(3.5m, s.InterestThisYear);                          // last year's interest doesn't count
        Assert.Equal(1, s.ScheduledCount);
        Assert.Equal(25m, s.Next?.Amount);                               // money coming in counts too
        Assert.Equal(checking.Id, s.Next?.FromAccount.Id);
    }
}

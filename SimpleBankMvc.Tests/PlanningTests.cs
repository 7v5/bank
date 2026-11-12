using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Tests;

public class ScheduledPaymentTests
{
    private static ScheduledPaymentService Schedules(TestBank bank) => new(bank.Db, bank.Banking, bank.Notifications, bank.Clock);
    private static DateOnly Today(TestBank bank) => DateOnly.FromDateTime(bank.Clock.GetLocalNow().DateTime);

    private static Payee AddPayee(TestBank bank, ApplicationUser owner)
    {
        var p = new Payee { OwnerId = owner.Id, Name = "City Power", AccountReference = "CP-1", Category = TransactionCategory.Bills };
        bank.Db.Payees.Add(p);
        bank.Db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task Monthly_transfer_runs_on_its_date_then_moves_to_next_month()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("ann");
        var from = bank.AddAccount(u, 500m);
        var to = bank.AddAccount(u, 0m, "Savings", AccountType.Savings);
        var s = Schedules(bank);
        var first = Today(bank).AddDays(1);
        var sched = await s.CreateAsync(u.Id, ScheduleKind.Transfer, from.Id, to.Id, null, 100m, null, first, ScheduleFrequency.Monthly);

        Assert.Equal(0, await s.RunDueAsync());           // not due yet
        bank.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await s.RunDueAsync());
        Assert.Equal(0, await s.RunDueAsync());           // never twice for the same date

        Assert.Equal(400m, bank.BalanceOf(from.Id));
        Assert.Equal(100m, bank.BalanceOf(to.Id));
        using var db = bank.Fresh();
        var saved = db.ScheduledPayments.Single(x => x.Id == sched.Id);
        Assert.Equal(first.AddMonths(1), saved.NextRunDate);
        Assert.Equal(ScheduleStatus.Active, saved.Status);
    }

    [Fact]
    public async Task One_time_bill_completes_after_paying()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("ben");
        var from = bank.AddAccount(u, 200m);
        var payee = AddPayee(bank, u);
        var s = Schedules(bank);
        await s.CreateAsync(u.Id, ScheduleKind.Bill, from.Id, null, payee.Id, 80m, "June bill", Today(bank).AddDays(2), ScheduleFrequency.Once);

        bank.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await s.RunDueAsync());
        Assert.Equal(120m, bank.BalanceOf(from.Id));
        using var db = bank.Fresh();
        Assert.Equal(ScheduleStatus.Completed, db.ScheduledPayments.Single().Status);
    }

    [Fact]
    public async Task Payment_that_cannot_be_made_is_paused_and_the_owner_is_told()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("cat");
        var from = bank.AddAccount(u, 50m);
        var to = bank.AddAccount(u, 0m, "Savings", AccountType.Savings);
        var s = Schedules(bank);
        await s.CreateAsync(u.Id, ScheduleKind.Transfer, from.Id, to.Id, null, 100m, null, Today(bank).AddDays(1), ScheduleFrequency.Weekly);

        bank.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await s.RunDueAsync());
        Assert.Equal(50m, bank.BalanceOf(from.Id));
        using var db = bank.Fresh();
        var saved = db.ScheduledPayments.Single();
        Assert.Equal(ScheduleStatus.Paused, saved.Status);
        Assert.Contains("Insufficient funds", saved.LastResult);
        Assert.Contains(db.Notifications.Where(n => n.UserId == u.Id), n => n.Title.Contains("didn't go through"));
    }

    [Fact]
    public async Task Rejects_past_dates_and_other_peoples_accounts()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("dan");
        var other = bank.AddUser("eve");
        var mine = bank.AddAccount(u, 100m);
        var theirs = bank.AddAccount(other, 100m);
        var s = Schedules(bank);
        await Assert.ThrowsAsync<BankingException>(() =>
            s.CreateAsync(u.Id, ScheduleKind.Transfer, mine.Id, theirs.Id, null, 10m, null, Today(bank).AddDays(3), ScheduleFrequency.Once));
        await Assert.ThrowsAsync<BankingException>(() =>
            s.CreateAsync(u.Id, ScheduleKind.Transfer, theirs.Id, mine.Id, null, 10m, null, Today(bank).AddDays(3), ScheduleFrequency.Once));
        await Assert.ThrowsAsync<BankingException>(() =>
            s.CreateAsync(u.Id, ScheduleKind.Transfer, mine.Id, mine.Id, null, 10m, null, Today(bank), ScheduleFrequency.Once));
    }

    [Fact]
    public async Task Paused_payment_does_not_run_and_resume_skips_missed_dates()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("fay");
        var from = bank.AddAccount(u, 1000m);
        var to = bank.AddAccount(u, 0m, "Savings", AccountType.Savings);
        var s = Schedules(bank);
        var sched = await s.CreateAsync(u.Id, ScheduleKind.Transfer, from.Id, to.Id, null, 10m, null, Today(bank).AddDays(1), ScheduleFrequency.Weekly);
        await s.PauseAsync(u.Id, sched.Id);

        bank.Clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(0, await s.RunDueAsync());
        await s.ResumeAsync(u.Id, sched.Id);
        Assert.Equal(0, await s.RunDueAsync());
        Assert.Equal(1000m, bank.BalanceOf(from.Id));
        using var db = bank.Fresh();
        Assert.True(db.ScheduledPayments.Single().NextRunDate > Today(bank));
    }

    [Fact]
    public void Run_key_is_stable_per_schedule_and_date()
    {
        var d = new DateOnly(2026, 7, 1);
        Assert.Equal(ScheduledPaymentService.RunKey(4, d), ScheduledPaymentService.RunKey(4, d));
        Assert.NotEqual(ScheduledPaymentService.RunKey(4, d), ScheduledPaymentService.RunKey(4, d.AddMonths(1)));
        Assert.NotEqual(ScheduledPaymentService.RunKey(4, d), ScheduledPaymentService.RunKey(5, d));
    }
}

public class BudgetStatementInterestLockTests
{
    [Fact]
    public async Task Budget_measures_this_months_spending_and_alerts_once()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("gus");
        var acct = bank.AddAccount(u, 1000m);
        var payee = new Payee { OwnerId = u.Id, Name = "Water Co", AccountReference = "W1", Category = TransactionCategory.Bills };
        bank.Db.Payees.Add(payee);
        bank.Db.SaveChanges();
        var budgets = new BudgetService(bank.Db, new InsightsService(bank.Db, bank.Clock), bank.Notifications, bank.Clock);

        await budgets.SetAsync(u.Id, TransactionCategory.Bills, 100m);
        await bank.Banking.PayBillAsync(u.Id, acct.Id, payee.Id, 95m, null);
        var status = Assert.Single(await budgets.StatusAsync(u.Id));
        Assert.Equal(95m, status.Spent);
        Assert.True(status.IsNear);

        Assert.Equal(1, await budgets.SendAlertsAsync());
        Assert.Equal(0, await budgets.SendAlertsAsync());
        await Assert.ThrowsAsync<BankingException>(() => budgets.SetAsync(u.Id, TransactionCategory.Income, 10m));
    }

    [Fact]
    public async Task Statement_opening_plus_activity_equals_closing()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("hal");
        var acct = bank.AddAccount(u, 300m); // opening deposit posted 30 days earlier, in May
        await bank.Banking.DepositAsync(u.Id, acct.Id, 50m, null);
        await bank.Banking.WithdrawAsync(u.Id, acct.Id, 20m, null);
        var statements = new StatementService(bank.Db, bank.Clock);

        var june = await statements.BuildAsync(u.Id, acct.Id, 2026, 6);
        Assert.NotNull(june);
        Assert.Equal(300m, june.Opening);
        Assert.Equal(330m, june.Closing);
        Assert.Equal(june.Opening + june.MoneyIn - june.MoneyOut, june.Closing);
        Assert.Null(await statements.BuildAsync("someone-else", acct.Id, 2026, 6));
    }

    [Fact]
    public async Task Interest_is_paid_once_per_month_on_savings_only()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("ivy");
        var savings = bank.AddAccount(u, 1200m, "Savings", AccountType.Savings);
        var checking = bank.AddAccount(u, 1200m);
        // Accounts opened before the month began earn interest for it.
        foreach (var a in bank.Db.BankAccounts) a.OpenedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bank.Db.SaveChanges();
        var interest = new InterestService(bank.Db, new LedgerPoster(bank.Db), bank.Notifications, bank.Clock);

        Assert.Equal(1, await interest.PostDueAsync());
        Assert.Equal(0, await interest.PostDueAsync());
        Assert.Equal(1202m, bank.BalanceOf(savings.Id)); // 1200 * 2% / 12
        Assert.Equal(1200m, bank.BalanceOf(checking.Id));
        Assert.Equal(2m, InterestService.MonthlyInterest(1200m, 0.02m));
        Assert.Equal(0m, InterestService.MonthlyInterest(-5m, 0.02m));
    }

    [Fact]
    public async Task Owner_lock_blocks_money_out_but_not_in()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("jo");
        var acct = bank.AddAccount(u, 100m);
        await bank.Banking.SetOwnerLockAsync(u.Id, acct.Id, true);

        var ex = await Assert.ThrowsAsync<BankingException>(() => bank.Banking.WithdrawAsync(u.Id, acct.Id, 10m, null));
        Assert.Contains("locked", ex.Message);
        await bank.Banking.DepositAsync(u.Id, acct.Id, 10m, null);
        Assert.Equal(110m, bank.BalanceOf(acct.Id));

        await bank.Banking.SetOwnerLockAsync(u.Id, acct.Id, false);
        await bank.Banking.WithdrawAsync(u.Id, acct.Id, 10m, null);
        Assert.Equal(100m, bank.BalanceOf(acct.Id));
    }

    [Fact]
    public async Task Customer_unlock_does_not_lift_an_admin_freeze()
    {
        using var bank = new TestBank();
        var u = bank.AddUser("kit");
        var acct = bank.AddAccount(u, 100m, frozen: true);
        await bank.Banking.SetOwnerLockAsync(u.Id, acct.Id, false);
        var ex = await Assert.ThrowsAsync<BankingException>(() => bank.Banking.WithdrawAsync(u.Id, acct.Id, 10m, null));
        Assert.Contains("frozen", ex.Message);
    }
}

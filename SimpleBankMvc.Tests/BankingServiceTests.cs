using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Tests;

public class BankingServiceTests : IDisposable
{
    private readonly TestBank _bank = new();
    public void Dispose() => _bank.Dispose();

    // ---------- Deposit / withdraw ----------

    [Fact]
    public async Task Deposit_adds_to_balance_and_writes_ledger_line()
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 100m);

        var result = await _bank.Banking.DepositAsync(user.Id, account.Id, 250.50m, null);

        Assert.Equal(350.50m, result.NewBalance);
        Assert.Equal(350.50m, _bank.BalanceOf(account.Id));
        using var db = _bank.Fresh();
        var line = db.Transactions.Single(t => t.Reference == result.Reference);
        Assert.Equal(TransactionType.Deposit, line.Type);
        Assert.Equal(250.50m, line.Amount);
        Assert.Equal(350.50m, line.BalanceAfter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_000.01)]
    [InlineData(1.005)]
    public async Task Deposit_rejects_invalid_amounts(decimal amount)
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 0m);
        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.DepositAsync(user.Id, account.Id, amount, null));
        Assert.Equal(0m, _bank.BalanceOf(account.Id));
    }

    [Fact]
    public async Task Withdraw_more_than_balance_is_rejected_and_nothing_changes()
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 50m);

        var ex = await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.WithdrawAsync(user.Id, account.Id, 50.01m, null));

        Assert.Contains("Insufficient funds", ex.Message);
        Assert.Equal(50m, _bank.BalanceOf(account.Id));
        // The failed attempt must not leave a mutated balance in the shared change tracker either.
        Assert.Equal(50m, (await _bank.Db.BankAccounts.SingleAsync(a => a.Id == account.Id)).Balance);
    }

    // ---------- Transfers ----------

    [Fact]
    public async Task Transfer_moves_money_and_ledger_lines_net_to_zero()
    {
        var user = _bank.AddUser("demo");
        var checking = _bank.AddAccount(user, 1000m);
        var savings = _bank.AddAccount(user, 0m, "Savings", AccountType.Savings);

        var result = await _bank.Banking.TransferAsync(user.Id, checking.Id, savings.Id, 400m, "Rainy day");

        Assert.Equal(600m, _bank.BalanceOf(checking.Id));
        Assert.Equal(400m, _bank.BalanceOf(savings.Id));
        using var db = _bank.Fresh();
        var lines = db.Transactions.Where(t => t.Reference == result.Reference).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(0m, lines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task Cannot_move_money_out_of_someone_elses_account()
    {
        var me = _bank.AddUser("demo");
        var victim = _bank.AddUser("alice");
        var mine = _bank.AddAccount(me, 0m);
        var theirs = _bank.AddAccount(victim, 500m);

        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.TransferAsync(me.Id, theirs.Id, mine.Id, 100m, null));
        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.WithdrawAsync(me.Id, theirs.Id, 100m, null));
        Assert.Equal(500m, _bank.BalanceOf(theirs.Id));
    }

    [Fact]
    public async Task Frozen_account_cannot_send_but_can_receive()
    {
        var user = _bank.AddUser("demo");
        var frozen = _bank.AddAccount(user, 500m, frozen: true);
        var other = _bank.AddAccount(user, 500m, "Other");

        var ex = await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.TransferAsync(user.Id, frozen.Id, other.Id, 10m, null));
        Assert.Contains("frozen", ex.Message);

        await _bank.Banking.TransferAsync(user.Id, other.Id, frozen.Id, 10m, null);
        Assert.Equal(510m, _bank.BalanceOf(frozen.Id));
    }

    // ---------- Person to person ----------

    [Fact]
    public async Task Send_to_user_lands_in_recipients_primary_account_and_notifies()
    {
        var demo = _bank.AddUser("demo");
        var alice = _bank.AddUser("alice", "Alice Smith");
        var from = _bank.AddAccount(demo, 300m, primary: true);
        _bank.AddAccount(alice, 0m, "Savings", AccountType.Savings);
        var alicePrimary = _bank.AddAccount(alice, 0m, "Checking", primary: true);

        await _bank.Banking.SendToUserAsync(demo.Id, from.Id, "@ALICE", 75m, "Pizza");

        Assert.Equal(225m, _bank.BalanceOf(from.Id));
        Assert.Equal(75m, _bank.BalanceOf(alicePrimary.Id));
        using var db = _bank.Fresh();
        Assert.Single(db.Notifications.Where(n => n.UserId == alice.Id));
    }

    [Fact]
    public async Task Send_by_email_works_and_sending_to_yourself_fails()
    {
        var demo = _bank.AddUser("demo");
        var bob = _bank.AddUser("bob");
        var from = _bank.AddAccount(demo, 100m, primary: true);
        _bank.AddAccount(bob, 0m, primary: true);

        await _bank.Banking.SendToUserAsync(demo.Id, from.Id, "BOB@test.local", 10m, null);
        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.SendToUserAsync(demo.Id, from.Id, "@demo", 10m, null));
        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.SendToUserAsync(demo.Id, from.Id, "@nobody", 10m, null));
    }

    [Fact]
    public async Task Daily_send_limit_is_enforced_over_a_rolling_24_hours()
    {
        var demo = _bank.AddUser("demo");
        var alice = _bank.AddUser("alice");
        var from = _bank.AddAccount(demo, 20_000m, primary: true);
        _bank.AddAccount(alice, 0m, primary: true);

        await _bank.Banking.SendToUserAsync(demo.Id, from.Id, "alice", 4_000m, null);
        var ex = await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.SendToUserAsync(demo.Id, from.Id, "alice", 1_500m, null));
        Assert.Contains("daily sending limit", ex.Message);

        _bank.Clock.Advance(TimeSpan.FromHours(25));
        await _bank.Banking.SendToUserAsync(demo.Id, from.Id, "alice", 1_500m, null);
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task Replaying_the_same_idempotency_key_is_rejected()
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 0m);
        var key = Guid.NewGuid();

        await _bank.Banking.DepositAsync(user.Id, account.Id, 100m, null, key);
        var ex = await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.DepositAsync(user.Id, account.Id, 100m, null, key));

        Assert.Contains("already submitted", ex.Message);
        Assert.Equal(100m, _bank.BalanceOf(account.Id));
    }

    [Fact]
    public async Task A_failed_request_does_not_burn_its_idempotency_key()
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 10m);
        var key = Guid.NewGuid();

        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.WithdrawAsync(user.Id, account.Id, 50m, null, key));
        await _bank.Banking.DepositAsync(user.Id, account.Id, 100m, null);
        await _bank.Banking.WithdrawAsync(user.Id, account.Id, 50m, null, key);
        Assert.Equal(60m, _bank.BalanceOf(account.Id));
    }

    // ---------- Account management ----------

    [Fact]
    public async Task Closing_requires_zero_balance_and_moves_primary_flag()
    {
        var user = _bank.AddUser("demo");
        var primary = _bank.AddAccount(user, 20m, primary: true);
        var savings = _bank.AddAccount(user, 0m, "Savings", AccountType.Savings);

        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.CloseAccountAsync(user.Id, primary.Id));
        await _bank.Banking.TransferAsync(user.Id, primary.Id, savings.Id, 20m, null);
        await _bank.Banking.CloseAccountAsync(user.Id, primary.Id);

        using var db = _bank.Fresh();
        Assert.False(db.BankAccounts.Single(a => a.Id == primary.Id).IsActive);
        Assert.True(db.BankAccounts.Single(a => a.Id == savings.Id).IsPrimary);
        await Assert.ThrowsAsync<BankingException>(() => _bank.Banking.CloseAccountAsync(user.Id, savings.Id));
    }

    // ---------- Storage ----------

    [Fact]
    public async Task Money_round_trips_through_cents_and_sorts_in_sql()
    {
        var user = _bank.AddUser("demo");
        var account = _bank.AddAccount(user, 0m);
        foreach (var amount in new[] { 0.10m, 1234.56m, 99.99m, 0.01m })
            await _bank.Banking.DepositAsync(user.Id, account.Id, amount, null);

        using var db = _bank.Fresh();
        var sorted = await db.Transactions.Where(t => t.BankAccountId == account.Id && t.Amount > 0.05m)
            .OrderByDescending(t => t.Amount).Select(t => t.Amount).ToListAsync();
        Assert.Equal([1234.56m, 99.99m, 0.10m], sorted);
        Assert.Equal(1334.66m, _bank.BalanceOf(account.Id));
    }
}

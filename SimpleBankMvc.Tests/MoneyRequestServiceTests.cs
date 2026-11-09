using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Tests;

public class MoneyRequestServiceTests : IDisposable
{
    private readonly TestBank _bank = new();
    public void Dispose() => _bank.Dispose();

    [Fact]
    public async Task Paying_a_request_moves_money_and_marks_it_paid()
    {
        var alice = _bank.AddUser("alice");
        var demo = _bank.AddUser("demo");
        var alicePrimary = _bank.AddAccount(alice, 0m, primary: true);
        var demoAccount = _bank.AddAccount(demo, 100m, primary: true);

        var request = await _bank.Requests.CreateAsync(alice.Id, "@demo", 40m, "Concert tickets");
        var result = await _bank.Requests.PayAsync(demo.Id, request.Id, demoAccount.Id);

        Assert.Equal(60m, _bank.BalanceOf(demoAccount.Id));
        Assert.Equal(40m, _bank.BalanceOf(alicePrimary.Id));
        using var db = _bank.Fresh();
        var saved = db.MoneyRequests.Single(r => r.Id == request.Id);
        Assert.Equal(MoneyRequestStatus.Paid, saved.Status);
        Assert.Equal(result.Reference, saved.PaymentReference);
    }

    [Fact]
    public async Task Failed_payment_leaves_request_pending()
    {
        var alice = _bank.AddUser("alice");
        var demo = _bank.AddUser("demo");
        _bank.AddAccount(alice, 0m, primary: true);
        var demoAccount = _bank.AddAccount(demo, 10m, primary: true);

        var request = await _bank.Requests.CreateAsync(alice.Id, "demo", 40m, null);
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.PayAsync(demo.Id, request.Id, demoAccount.Id));

        using var db = _bank.Fresh();
        Assert.Equal(MoneyRequestStatus.Pending, db.MoneyRequests.Single().Status);
        Assert.Equal(10m, _bank.BalanceOf(demoAccount.Id));
    }

    [Fact]
    public async Task Only_pending_requests_change_state_and_only_by_the_right_party()
    {
        var alice = _bank.AddUser("alice");
        var demo = _bank.AddUser("demo");
        _bank.AddAccount(alice, 0m, primary: true);
        var demoAccount = _bank.AddAccount(demo, 100m, primary: true);

        var request = await _bank.Requests.CreateAsync(alice.Id, "demo", 5m, null);

        // The requester can't decline their own request, and the payer can't cancel it.
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.DeclineAsync(alice.Id, request.Id));
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.CancelAsync(demo.Id, request.Id));

        await _bank.Requests.DeclineAsync(demo.Id, request.Id);
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.PayAsync(demo.Id, request.Id, demoAccount.Id));
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.CancelAsync(alice.Id, request.Id));
        Assert.Equal(100m, _bank.BalanceOf(demoAccount.Id));
    }

    [Fact]
    public async Task Cannot_request_from_yourself()
    {
        var demo = _bank.AddUser("demo");
        await Assert.ThrowsAsync<BankingException>(() => _bank.Requests.CreateAsync(demo.Id, "@demo", 5m, null));
    }
}

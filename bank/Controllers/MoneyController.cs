using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

/// <summary>Deposit, withdraw, transfer between your own accounts, and send to another customer.</summary>
public class MoneyController(ApplicationDbContext db, IBankingService banking, IScheduledPaymentService schedules, TimeProvider clock) : CustomerController
{
    public IActionResult Index() => View();

    // ---------- Deposit ----------
    public Task<IActionResult> Deposit(int? accountId) => Form(new MoneyFormViewModel { Kind = MoneyKind.Deposit, ToAccountId = accountId });

    [HttpPost]
    public Task<IActionResult> Deposit(MoneyFormViewModel vm) => Submit(vm, MoneyKind.Deposit, async () =>
    {
        var r = await banking.DepositAsync(Uid, vm.ToAccountId ?? 0, vm.Amount!.Value, vm.Memo, vm.IdempotencyKey);
        var to = await AccountName(vm.ToAccountId);
        return new ReceiptViewModel("Deposit", "Deposit complete", vm.Amount.Value, null, to, vm.Memo, r.Reference, r.NewBalance, Now, vm.ToAccountId);
    });

    // ---------- Withdraw ----------
    public Task<IActionResult> Withdraw(int? accountId) => Form(new MoneyFormViewModel { Kind = MoneyKind.Withdraw, FromAccountId = accountId });

    [HttpPost]
    public Task<IActionResult> Withdraw(MoneyFormViewModel vm) => Submit(vm, MoneyKind.Withdraw, async () =>
    {
        var r = await banking.WithdrawAsync(Uid, vm.FromAccountId ?? 0, vm.Amount!.Value, vm.Memo, vm.IdempotencyKey);
        var from = await AccountName(vm.FromAccountId);
        return new ReceiptViewModel("Withdraw", "Withdrawal complete", vm.Amount.Value, from, "Cash", vm.Memo, r.Reference, r.NewBalance, Now, vm.FromAccountId);
    });

    // ---------- Transfer ----------
    public Task<IActionResult> Transfer(int? from, int? to) => Form(new MoneyFormViewModel { Kind = MoneyKind.Transfer, FromAccountId = from, ToAccountId = to });

    [HttpPost]
    public Task<IActionResult> Transfer(MoneyFormViewModel vm) => vm.IsScheduled ? ScheduleTransfer(vm) : Submit(vm, MoneyKind.Transfer, async () =>
    {
        var r = await banking.TransferAsync(Uid, vm.FromAccountId ?? 0, vm.ToAccountId ?? 0, vm.Amount!.Value, vm.Memo, vm.IdempotencyKey);
        return new ReceiptViewModel("Transfer", "Transfer complete", vm.Amount.Value, await AccountName(vm.FromAccountId),
            await AccountName(vm.ToAccountId), vm.Memo, r.Reference, r.NewBalance, Now, vm.FromAccountId);
    });

    // ---------- Send to a person ----------
    public Task<IActionResult> Send(string? to, decimal? amount, string? note) =>
        Form(new MoneyFormViewModel { Kind = MoneyKind.Send, Recipient = to, Amount = amount, Memo = note });

    [HttpPost]
    public Task<IActionResult> Send(MoneyFormViewModel vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Recipient)) ModelState.AddModelError(nameof(vm.Recipient), "Choose who to pay.");
        return Submit(vm, MoneyKind.Send, async () =>
        {
            var r = await banking.SendToUserAsync(Uid, vm.FromAccountId ?? 0, vm.Recipient!, vm.Amount!.Value, vm.Memo, vm.IdempotencyKey);
            var recipient = await banking.FindRecipientAsync(vm.Recipient!);
            return new ReceiptViewModel("Send", $"Sent to {recipient?.FirstName}", vm.Amount.Value, await AccountName(vm.FromAccountId),
                $"{recipient?.FullName} (@{recipient?.Handle})", vm.Memo, r.Reference, r.NewBalance, Now, vm.FromAccountId);
        });
    }

    /// <summary>"On a date" was chosen: save the transfer as a scheduled payment instead of moving money now.</summary>
    private async Task<IActionResult> ScheduleTransfer(MoneyFormViewModel vm)
    {
        vm.Kind = MoneyKind.Transfer;
        if (vm.RunDate is null) ModelState.AddModelError("", "Choose a date for the transfer.");
        if (ModelState.IsValid && await TryBankAsync(() => schedules.CreateAsync(Uid, Models.ScheduleKind.Transfer, vm.FromAccountId ?? 0, vm.ToAccountId,
                null, vm.Amount!.Value, vm.Memo, vm.RunDate!.Value, vm.Frequency)))
        {
            Flash("Transfer scheduled", $"{Money.Format(vm.Amount!.Value)} on {vm.RunDate:MMMM d, yyyy}. {ScheduledPaymentService.FrequencyLabel(vm.Frequency)}.");
            return RedirectToAction("Index", "Scheduled");
        }
        await Fill(vm);
        return View("Flow", vm);
    }

    // ---------- Receipt ----------
    public IActionResult Done()
    {
        if (TempData["Receipt"] is not string json) return RedirectToAction("Index", "Dashboard");
        return View(JsonSerializer.Deserialize<ReceiptViewModel>(json));
    }

    // ---------- Helpers ----------
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<IActionResult> Form(MoneyFormViewModel vm)
    {
        await Fill(vm);
        if (vm.Accounts.Count == 0) { FlashError("Open an account first"); return RedirectToAction("Open", "Accounts"); }
        // Sensible defaults: primary for spending, the other account for the destination.
        vm.FromAccountId ??= vm.Kind is MoneyKind.Withdraw or MoneyKind.Transfer or MoneyKind.Send ? vm.Accounts.FirstOrDefault(a => a.IsPrimary)?.Id ?? vm.Accounts[0].Id : null;
        vm.ToAccountId ??= vm.Kind switch
        {
            MoneyKind.Deposit => vm.Accounts.FirstOrDefault(a => a.IsPrimary)?.Id ?? vm.Accounts[0].Id,
            MoneyKind.Transfer => vm.Accounts.FirstOrDefault(a => a.Id != vm.FromAccountId)?.Id,
            _ => null
        };
        ModelState.Clear();
        return View("Flow", vm);
    }

    private async Task<IActionResult> Submit(MoneyFormViewModel vm, MoneyKind kind, Func<Task<ReceiptViewModel>> operation)
    {
        vm.Kind = kind;
        ReceiptViewModel? receipt = null;
        if (ModelState.IsValid && await TryBankAsync(async () => receipt = await operation()))
        {
            TempData["Receipt"] = JsonSerializer.Serialize(receipt);
            return RedirectToAction(nameof(Done));
        }
        await Fill(vm);
        vm.IdempotencyKey = Guid.NewGuid(); // the failed attempt didn't use its key, but a fresh one avoids any doubt
        return View("Flow", vm);
    }

    private async Task Fill(MoneyFormViewModel vm)
    {
        vm.Accounts = await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == Uid && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Type).ThenBy(a => a.Id).ToListAsync();
        if (vm.Kind == MoneyKind.Send)
        {
            vm.SentToday = await banking.SentInLast24HoursAsync(Uid);
            if (!string.IsNullOrWhiteSpace(vm.Recipient)) vm.RecipientUser = await banking.FindRecipientAsync(vm.Recipient);
            if (vm.RecipientUser?.Id == Uid) vm.RecipientUser = null;
        }
    }

    private async Task<string?> AccountName(int? id) =>
        id is null ? null : await db.BankAccounts.Where(a => a.Id == id && a.OwnerId == Uid).Select(a => a.Name).SingleOrDefaultAsync();
}

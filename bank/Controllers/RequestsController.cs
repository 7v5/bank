using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

public class RequestMoneyViewModel
{
    [Required(ErrorMessage = "Choose who to ask."), StringLength(256)] public string Payer { get; set; } = "";
    [Required(ErrorMessage = "Enter an amount."), Range(typeof(decimal), "0.01", "5000", ErrorMessage = "Enter an amount between $0.01 and $5,000.")]
    public decimal? Amount { get; set; }
    [StringLength(140)] public string? Note { get; set; }
}

public class PayRequestViewModel
{
    public MoneyRequest? Request { get; set; }
    [Required] public int FromAccountId { get; set; }
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();
    public List<BankAccount> Accounts { get; set; } = [];
}

/// <summary>Ask other customers for money, and pay or decline what they ask of you.</summary>
public class RequestsController(ApplicationDbContext db, IMoneyRequestService requests, TimeProvider clock) : CustomerController
{
    public async Task<IActionResult> Index(string? tab)
    {
        ViewBag.Tab = tab == "outgoing" ? "outgoing" : "incoming";
        ViewBag.Incoming = await requests.IncomingAsync(Uid);
        ViewBag.Outgoing = await requests.OutgoingAsync(Uid);
        return View();
    }

    /// <summary>The form can arrive filled in, for example from "Split this" on a transaction.</summary>
    public IActionResult Create(string? from, decimal? amount, string? note) => View(new RequestMoneyViewModel
    {
        Payer = from ?? "",
        Amount = amount is > 0 and <= 5000 ? decimal.Round(amount.Value, 2) : null,
        Note = note is { Length: > 140 } ? note[..140] : note
    });

    [HttpPost]
    public async Task<IActionResult> Create(RequestMoneyViewModel vm)
    {
        MoneyRequest? created = null;
        if (ModelState.IsValid && await TryBankAsync(async () => created = await requests.CreateAsync(Uid, vm.Payer, vm.Amount!.Value, vm.Note)))
        {
            TempData["Receipt"] = JsonSerializer.Serialize(new ReceiptViewModel("Request", $"Request sent to {created!.Payer.FirstName}", created.Amount, null,
                $"{created.Payer.FullName} (@{created.Payer.Handle})", created.Note, $"REQ-{created.Id:D6}", null, clock.GetUtcNow().UtcDateTime, null));
            return RedirectToAction("Done", "Money");
        }
        return View(vm);
    }

    public async Task<IActionResult> Pay(int id)
    {
        var vm = await FillPay(new PayRequestViewModel(), id);
        if (vm.Request is null) return NotFound();
        vm.FromAccountId = vm.Accounts.FirstOrDefault(a => a.IsPrimary && !a.IsBlocked)?.Id ?? vm.Accounts.FirstOrDefault(a => !a.IsBlocked)?.Id ?? 0;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Pay(int id, PayRequestViewModel vm)
    {
        PostingResult? result = null;
        if (await TryBankAsync(async () => result = await requests.PayAsync(Uid, id, vm.FromAccountId, vm.IdempotencyKey)))
        {
            var r = await db.MoneyRequests.AsNoTracking().Include(x => x.Requester).SingleAsync(x => x.Id == id);
            var from = await db.BankAccounts.AsNoTracking().Where(a => a.Id == vm.FromAccountId && a.OwnerId == Uid).Select(a => a.Name).SingleOrDefaultAsync();
            TempData["Receipt"] = JsonSerializer.Serialize(new ReceiptViewModel("Send", $"Paid {r.Requester.FirstName}", r.Amount, from,
                $"{r.Requester.FullName} (@{r.Requester.Handle})", r.Note, result!.Reference, result.NewBalance, clock.GetUtcNow().UtcDateTime, vm.FromAccountId));
            return RedirectToAction("Done", "Money");
        }
        await FillPay(vm, id);
        if (vm.Request is null) return NotFound();
        vm.IdempotencyKey = Guid.NewGuid();
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Decline(int id)
    {
        if (await TryBankAsync(() => requests.DeclineAsync(Uid, id))) Flash("Request declined");
        else FlashError("Couldn't decline", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id)
    {
        if (await TryBankAsync(() => requests.CancelAsync(Uid, id))) Flash("Request cancelled");
        else FlashError("Couldn't cancel", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Index), new { tab = "outgoing" });
    }

    private async Task<PayRequestViewModel> FillPay(PayRequestViewModel vm, int id)
    {
        vm.Request = await db.MoneyRequests.AsNoTracking().Include(r => r.Requester)
            .SingleOrDefaultAsync(r => r.Id == id && r.PayerId == Uid);
        vm.Accounts = await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == Uid && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id).ToListAsync();
        return vm;
    }
}

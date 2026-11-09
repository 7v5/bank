using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

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
public class RequestsController(ApplicationDbContext db, IMoneyRequestService requests) : CustomerController
{
    public async Task<IActionResult> Index(string? tab)
    {
        ViewBag.Tab = tab == "outgoing" ? "outgoing" : "incoming";
        ViewBag.Incoming = await requests.IncomingAsync(Uid);
        ViewBag.Outgoing = await requests.OutgoingAsync(Uid);
        return View();
    }

    public IActionResult Create(string? from) => View(new RequestMoneyViewModel { Payer = from ?? "" });

    [HttpPost]
    public async Task<IActionResult> Create(RequestMoneyViewModel vm)
    {
        MoneyRequest? created = null;
        if (ModelState.IsValid && await TryBankAsync(async () => created = await requests.CreateAsync(Uid, vm.Payer, vm.Amount!.Value, vm.Note)))
        {
            Flash($"Requested {Money.Format(created!.Amount)} from {created.Payer.FirstName}", "We'll let you know when they respond.");
            return RedirectToAction(nameof(Index), new { tab = "outgoing" });
        }
        return View(vm);
    }

    public async Task<IActionResult> Pay(int id)
    {
        var vm = await FillPay(new PayRequestViewModel(), id);
        if (vm.Request is null) return NotFound();
        vm.FromAccountId = vm.Accounts.FirstOrDefault(a => a.IsPrimary)?.Id ?? vm.Accounts.FirstOrDefault()?.Id ?? 0;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Pay(int id, PayRequestViewModel vm)
    {
        if (await TryBankAsync(() => requests.PayAsync(Uid, id, vm.FromAccountId, vm.IdempotencyKey)))
        {
            var r = await db.MoneyRequests.AsNoTracking().Include(x => x.Requester).SingleAsync(x => x.Id == id);
            Flash($"Paid {r.Requester.FirstName} {Money.Format(r.Amount)}");
            return RedirectToAction(nameof(Index));
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

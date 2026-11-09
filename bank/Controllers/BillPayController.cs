using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

public class PayeeViewModel
{
    [Required(ErrorMessage = "Enter the payee's name."), StringLength(100)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "Enter your account number with them."), StringLength(120), Display(Name = "Your account number with them")]
    public string AccountReference { get; set; } = "";
    public TransactionCategory Category { get; set; } = TransactionCategory.Bills;
}

public class BillPayViewModel
{
    [Required(ErrorMessage = "Choose a payee.")] public int? PayeeId { get; set; }
    [Required(ErrorMessage = "Choose an account.")] public int? FromAccountId { get; set; }
    [Required(ErrorMessage = "Enter an amount."), Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "Enter an amount greater than zero.")]
    public decimal? Amount { get; set; }
    [StringLength(140)] public string? Memo { get; set; }
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();
    public List<BankAccount> Accounts { get; set; } = [];
    public List<Payee> Payees { get; set; } = [];
}

/// <summary>Saved payees and simulated bill payments.</summary>
public class BillPayController(ApplicationDbContext db, IBankingService banking, TimeProvider clock) : CustomerController
{
    public async Task<IActionResult> Index()
    {
        var payees = await db.Payees.AsNoTracking().Where(p => p.OwnerId == Uid).OrderBy(p => p.Name).ToListAsync();
        var names = payees.Select(p => p.Name).ToList();
        // Last payment to each payee, for the "last paid" hint.
        var last = await db.Transactions.AsNoTracking()
            .Where(t => t.BankAccount.OwnerId == Uid && t.Type == TransactionType.BillPayment && t.CounterpartyName != null && names.Contains(t.CounterpartyName))
            .GroupBy(t => t.CounterpartyName!)
            .Select(g => new { Name = g.Key, When = g.Max(t => t.PostedUtc) })
            .ToDictionaryAsync(x => x.Name, x => x.When);
        ViewBag.LastPaid = last;
        return View(payees);
    }

    public IActionResult AddPayee() => View(new PayeeViewModel());

    [HttpPost]
    public async Task<IActionResult> AddPayee(PayeeViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        db.Payees.Add(new Payee { OwnerId = Uid, Name = vm.Name.Trim(), AccountReference = vm.AccountReference.Trim(), Category = vm.Category });
        await db.SaveChangesAsync();
        Flash($"{vm.Name.Trim()} added");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> DeletePayee(int id)
    {
        var removed = await db.Payees.Where(p => p.Id == id && p.OwnerId == Uid).ExecuteDeleteAsync();
        if (removed > 0) Flash("Payee removed");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Pay(int? id)
    {
        var vm = await Fill(new BillPayViewModel { PayeeId = id });
        vm.FromAccountId = vm.Accounts.FirstOrDefault(a => a.IsPrimary)?.Id ?? vm.Accounts.FirstOrDefault()?.Id;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Pay(BillPayViewModel vm)
    {
        PostingResult? result = null;
        if (ModelState.IsValid && await TryBankAsync(async () => result = await banking.PayBillAsync(Uid, vm.FromAccountId!.Value, vm.PayeeId!.Value, vm.Amount!.Value, vm.Memo, vm.IdempotencyKey)))
        {
            var payee = await db.Payees.AsNoTracking().SingleAsync(p => p.Id == vm.PayeeId);
            var from = await db.BankAccounts.AsNoTracking().Where(a => a.Id == vm.FromAccountId).Select(a => a.Name).SingleAsync();
            TempData["Receipt"] = JsonSerializer.Serialize(new ReceiptViewModel("Bill", $"Paid {payee.Name}", vm.Amount!.Value, from, payee.Name,
                vm.Memo, result!.Reference, result.NewBalance, clock.GetUtcNow().UtcDateTime, vm.FromAccountId));
            return RedirectToAction("Done", "Money");
        }
        await Fill(vm);
        vm.IdempotencyKey = Guid.NewGuid();
        return View(vm);
    }

    private async Task<BillPayViewModel> Fill(BillPayViewModel vm)
    {
        vm.Accounts = await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == Uid && a.IsActive).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id).ToListAsync();
        vm.Payees = await db.Payees.AsNoTracking().Where(p => p.OwnerId == Uid).OrderBy(p => p.Name).ToListAsync();
        return vm;
    }
}

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
    /// <summary>Set when editing a saved payee; null when adding a new one.</summary>
    public int? Id { get; set; }
    [Required(ErrorMessage = "Enter the payee's name."), StringLength(100)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "Enter your account number with them."), StringLength(120), Display(Name = "Your account number with them")]
    public string AccountReference { get; set; } = "";
    public TransactionCategory Category { get; set; } = TransactionCategory.Bills;
}

public class BillPayViewModel : IScheduleInput
{
    [Required(ErrorMessage = "Choose a payee.")] public int? PayeeId { get; set; }
    [Required(ErrorMessage = "Choose an account.")] public int? FromAccountId { get; set; }
    [Required(ErrorMessage = "Enter an amount."), Range(typeof(decimal), "0.01", "1000000", ErrorMessage = "Enter an amount greater than zero.")]
    public decimal? Amount { get; set; }
    [StringLength(140)] public string? Memo { get; set; }
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();

    // Scheduling: "later" means pay on RunDate instead of now.
    public string? When { get; set; }
    public DateOnly? RunDate { get; set; }
    public ScheduleFrequency Frequency { get; set; }
    public bool IsScheduled => When == "later";

    public List<BankAccount> Accounts { get; set; } = [];
    public List<Payee> Payees { get; set; } = [];
}

public record BillPayIndexViewModel(List<Payee> Payees, Dictionary<string, DateTime> LastPaid, decimal PaidThisMonth, int PaymentsThisMonth, List<ScheduledPayment> Upcoming);

/// <summary>Saved payees and simulated bill payments, paid now or scheduled for a date.</summary>
public class BillPayController(ApplicationDbContext db, IBankingService banking, IScheduledPaymentService schedules, IAuditService audit,
    INotificationService notifications, TimeProvider clock) : CustomerController
{
    public static readonly TransactionCategory[] PayeeCategories =
        [TransactionCategory.Bills, TransactionCategory.Shopping, TransactionCategory.Transport, TransactionCategory.Entertainment, TransactionCategory.Other];

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
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var paid = await db.Transactions.Where(t => t.BankAccount.OwnerId == Uid && t.Type == TransactionType.BillPayment && t.PostedUtc >= monthStart)
            .Select(t => t.Amount).ToListAsync();
        var upcoming = (await schedules.ListAsync(Uid, activeOnly: true)).Where(s => s.Kind == ScheduleKind.Bill).Take(5).ToList();
        return View(new BillPayIndexViewModel(payees, last, -paid.Sum(), paid.Count, upcoming));
    }

    public IActionResult AddPayee() => View(new PayeeViewModel());

    [HttpPost]
    public async Task<IActionResult> AddPayee(PayeeViewModel vm)
    {
        vm.Id = null;
        if (!PayeeCategories.Contains(vm.Category)) vm.Category = TransactionCategory.Bills;
        if (!ModelState.IsValid) return View(vm);
        var payee = new Payee { OwnerId = Uid, Name = vm.Name.Trim(), AccountReference = vm.AccountReference.Trim(), Category = vm.Category };
        db.Payees.Add(payee);
        // A new place money can go is worth telling the customer about.
        notifications.Add(Uid, $"{payee.Name} added as a payee", "If you didn't add this payee, remove it and change your password.", "building", "/BillPay");
        await db.SaveChangesAsync();
        await audit.LogAsync(Uid, "security.payee-added", "Payee", payee.Id.ToString(), $"Added payee {payee.Name}");
        Flash($"{payee.Name} added", "You can pay them now or set up a scheduled payment.");
        return RedirectToAction(nameof(Pay), new { id = payee.Id });
    }

    public async Task<IActionResult> EditPayee(int id)
    {
        var p = await db.Payees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == Uid);
        if (p is null) return NotFound();
        return View("AddPayee", new PayeeViewModel { Id = p.Id, Name = p.Name, AccountReference = p.AccountReference, Category = p.Category });
    }

    [HttpPost]
    public async Task<IActionResult> EditPayee(int id, PayeeViewModel vm)
    {
        var p = await db.Payees.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == Uid);
        if (p is null) return NotFound();
        vm.Id = id;
        if (!PayeeCategories.Contains(vm.Category)) vm.Category = TransactionCategory.Bills;
        if (!ModelState.IsValid) return View("AddPayee", vm);
        p.Name = vm.Name.Trim();
        p.AccountReference = vm.AccountReference.Trim();
        p.Category = vm.Category;
        await db.SaveChangesAsync();
        Flash($"{p.Name} updated");
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
        vm.FromAccountId = vm.Accounts.FirstOrDefault(a => a.IsPrimary && !a.IsBlocked)?.Id ?? vm.Accounts.FirstOrDefault(a => !a.IsBlocked)?.Id;
        if (vm.Payees.Count == 1) vm.PayeeId ??= vm.Payees[0].Id;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Pay(BillPayViewModel vm)
    {
        if (vm.IsScheduled)
        {
            if (vm.RunDate is null) ModelState.AddModelError("", "Choose a date for the payment.");
            if (ModelState.IsValid && await TryBankAsync(() => schedules.CreateAsync(Uid, ScheduleKind.Bill, vm.FromAccountId!.Value, null, vm.PayeeId,
                    vm.Amount!.Value, vm.Memo, vm.RunDate!.Value, vm.Frequency)))
            {
                Flash("Payment scheduled", $"{Money.Format(vm.Amount!.Value)} on {vm.RunDate:MMMM d, yyyy}. {ScheduledPaymentService.FrequencyLabel(vm.Frequency)}.");
                return RedirectToAction("Index", "Scheduled");
            }
        }
        else
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

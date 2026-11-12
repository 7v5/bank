using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

public class AccountsController(ApplicationDbContext db, IBankingService banking, ITransactionQueryService ledger, IStatementService statements,
    IInterestService interest, IAuditService audit, TimeProvider clock) : CustomerController
{
    public async Task<IActionResult> Index()
    {
        var accounts = await db.BankAccounts.AsNoTracking()
            .Where(a => a.OwnerId == Uid)
            .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.IsPrimary).ThenBy(a => a.Type).ThenBy(a => a.Id)
            .ToListAsync();
        return View(accounts);
    }

    public async Task<IActionResult> Details(int id, [FromQuery] TransactionQuery query)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == Uid);
        if (account is null) return NotFound();

        query.AccountId = id;
        var history = await ledger.SearchAsync(query, Uid);
        if (Request.Headers.ContainsKey("X-Partial")) return PartialView("_TransactionResults", new TransactionResultsModel(history, query, nameof(Details), id));

        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var last30 = await db.Transactions.Where(t => t.BankAccountId == id && t.PostedUtc >= since).Select(t => t.Amount).ToListAsync();
        var rate = InterestService.RateFor(account.Type);
        ViewBag.InterestRate = rate;
        ViewBag.InterestYtd = rate > 0 ? await interest.PaidThisYearAsync(id) : 0m;
        ViewBag.InterestNext = InterestService.MonthlyInterest(account.Balance, rate);
        return View(new AccountDetailsViewModel
        {
            Account = account, History = history, Query = query,
            In30 = last30.Where(a => a > 0).Sum(), Out30 = -last30.Where(a => a < 0).Sum()
        });
    }

    public IActionResult Open(AccountType? type) => View(new OpenAccountViewModel { Type = type ?? AccountType.Savings });

    [HttpPost]
    public async Task<IActionResult> Open(OpenAccountViewModel vm)
    {
        if (vm.Type == AccountType.Goal && vm.GoalAmount is null)
            ModelState.AddModelError(nameof(vm.GoalAmount), "Set a target amount for your goal.");
        BankAccount? account = null;
        if (ModelState.IsValid && await TryBankAsync(async () => account = await banking.OpenAccountAsync(Uid, vm.Name, vm.Type, vm.GoalAmount)))
        {
            return RedirectToAction(nameof(Opened), new { id = account!.Id });
        }
        return View(vm);
    }

    /// <summary>Last step of the Open account flow: confirms the new account and offers the next moves.</summary>
    public async Task<IActionResult> Opened(int id)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == Uid && a.IsActive);
        return account is null ? NotFound() : View(account);
    }

    [HttpPost]
    public async Task<IActionResult> Rename(int id, RenameAccountViewModel vm)
    {
        if (!ModelState.IsValid) FlashError("Couldn't save changes", "Check the name and target amount.");
        else if (await TryBankAsync(() => banking.RenameAsync(Uid, id, vm.Name, vm.GoalAmount))) Flash("Changes saved");
        else FlashError("Couldn't save changes", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> MakePrimary(int id)
    {
        if (await TryBankAsync(() => banking.SetPrimaryAsync(Uid, id))) Flash("Primary account updated", "Money people send you will land here.");
        else FlashError("Couldn't change your primary account", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>A month's statement for one account. Printing the page gives a paper copy or a PDF.</summary>
    public async Task<IActionResult> Statement(int id, string? month)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == Uid);
        if (account is null) return NotFound();
        var months = statements.MonthsFor(account);
        var chosen = DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var m) && months.Contains(m) ? m : months[0];
        var statement = await statements.BuildAsync(Uid, id, chosen.Year, chosen.Month);
        if (statement is null) return NotFound();
        ViewBag.Months = months;
        return View(statement);
    }

    /// <summary>The customer's own lock. It stops money leaving the account; an admin freeze is separate and stays in place.</summary>
    [HttpPost]
    public async Task<IActionResult> SetLock(int id, bool locked)
    {
        if (await TryBankAsync(() => banking.SetOwnerLockAsync(Uid, id, locked)))
        {
            await audit.LogAsync(Uid, locked ? "security.account-locked" : "security.account-unlocked", "BankAccount", id.ToString(),
                locked ? "Locked an account" : "Unlocked an account");
            Flash(locked ? "Account locked" : "Account unlocked", locked ? "No money can leave it until you unlock it." : "You can move money out again.");
        }
        else FlashError("Couldn't change the lock", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Close(int id)
    {
        if (await TryBankAsync(() => banking.CloseAccountAsync(Uid, id)))
        {
            Flash("Account closed");
            return RedirectToAction(nameof(Index));
        }
        FlashError("Couldn't close this account", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Details), new { id });
    }
}

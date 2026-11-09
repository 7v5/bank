using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

public class AccountsController(ApplicationDbContext db, IBankingService banking, ITransactionQueryService ledger, TimeProvider clock) : CustomerController
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
            Flash($"{account!.Name} is open", "Move some money in to get started.");
            return RedirectToAction(nameof(Details), new { id = account.Id });
        }
        return View(vm);
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

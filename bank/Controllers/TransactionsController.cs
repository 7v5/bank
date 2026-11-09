using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

/// <summary>Full transaction history across all of the user's accounts, with filters, sorting and pagination.</summary>
public class TransactionsController(ApplicationDbContext db, ITransactionQueryService ledger) : CustomerController
{
    public async Task<IActionResult> Index([FromQuery] TransactionQuery query)
    {
        var results = await ledger.SearchAsync(query, Uid);
        var model = new TransactionResultsModel(results, query, nameof(Index), ShowAccount: true);
        // The filter UI swaps just the results in place (no full page load).
        if (Request.Headers.ContainsKey("X-Partial")) return PartialView("_TransactionResults", model);

        ViewBag.Accounts = await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == Uid)
            .OrderByDescending(a => a.IsActive).ThenBy(a => a.Id).ToListAsync();
        return View(model);
    }

    /// <summary>Detail drawer content for one transaction (and the other side of a transfer).</summary>
    public async Task<IActionResult> Details(long id)
    {
        var tx = await db.Transactions.AsNoTracking().Include(t => t.BankAccount)
            .SingleOrDefaultAsync(t => t.Id == id && t.BankAccount.OwnerId == Uid);
        if (tx is null) return NotFound();
        var related = await db.Transactions.AsNoTracking().Include(t => t.BankAccount)
            .Where(t => t.Reference == tx.Reference && t.Id != tx.Id && t.BankAccount.OwnerId == Uid)
            .FirstOrDefaultAsync();
        ViewBag.Related = related;
        if (tx.Type is Models.TransactionType.P2PReceived or Models.TransactionType.P2PSent)
            ViewBag.CounterpartyHandle = await db.Transactions.Where(t => t.Reference == tx.Reference && t.Id != tx.Id)
                .Select(t => t.BankAccount.Owner.Handle).FirstOrDefaultAsync();
        return PartialView("_TransactionDetails", tx);
    }
}

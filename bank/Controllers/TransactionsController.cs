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

    /// <summary>Everything that matches the current Activity filters, as a spreadsheet-friendly CSV file.</summary>
    public async Task<IActionResult> Export([FromQuery] TransactionQuery query)
    {
        const int maxRows = 5000;
        var csv = new System.Text.StringBuilder("Date,Account,Type,Category,Description,Other party,Amount,Balance after,Reference\r\n");
        query.PageSize = TransactionQuery.PageSizes[^1];
        var rows = 0;
        for (query.Page = 1; rows < maxRows; query.Page++)
        {
            var page = await ledger.SearchAsync(query, Uid);
            foreach (var t in page.Items)
            {
                csv.AppendJoin(',', Csv(t.PostedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), Csv(t.BankAccount?.Name), Csv(Infrastructure.Ui.TypeLabel(t.Type)),
                    Csv(t.Category.ToString()), Csv(t.Description), Csv(t.CounterpartyName),
                    t.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    t.BalanceAfter.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), Csv(t.Reference)).Append("\r\n");
                rows++;
            }
            if (!page.HasNext) break;
        }
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv", $"simplebank-activity-{DateTime.Now:yyyy-MM-dd}.csv");
    }

    /// <summary>Quotes a CSV cell, and stops spreadsheet apps from running text that starts like a formula.</summary>
    private static string Csv(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
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

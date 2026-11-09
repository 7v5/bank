using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Infrastructure;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Areas.Admin.Controllers;

[Area("Admin"), Authorize(Roles = DbInitializer.AdminRole)]
public abstract class AdminController : Controller
{
    protected string Uid => User.UserId();

    protected async Task Run(Func<Task> action, string success)
    {
        try { await action(); TempData["Success"] = success; }
        catch (BankingException ex) { TempData["Error"] = "Couldn't complete that"; TempData["ErrorBody"] = ex.Message; }
    }
}

public class DashboardController(IAdminService admin, ApplicationDbContext db) : AdminController
{
    public async Task<IActionResult> Index()
    {
        ViewBag.Recent = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(8).ToListAsync();
        return View(await admin.StatsAsync());
    }
}

public class UsersController(IAdminService admin, ApplicationDbContext db, ITransactionQueryService ledger) : AdminController
{
    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        ViewBag.Q = q;
        return View(await admin.CustomersAsync(q, page));
    }

    public async Task<IActionResult> Details(string id, [FromQuery] TransactionQuery query)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();
        var ownerFilter = id;
        var history = await ledger.SearchAsync(query, ownerFilter);
        var model = new TransactionResultsModel(history, query, nameof(Details), ShowAccount: true);
        if (Request.Headers.ContainsKey("X-Partial")) return PartialView("_TransactionResults", model);
        ViewBag.Customer = user;
        ViewBag.Accounts = await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == id).OrderByDescending(a => a.IsActive).ThenBy(a => a.Id).ToListAsync();
        ViewBag.IsLocked = user.LockoutEnd > DateTimeOffset.UtcNow;
        ViewBag.Audit = await db.AuditLogs.AsNoTracking().Where(a => a.TargetId == id || a.ActorId == id).OrderByDescending(a => a.Id).Take(10).ToListAsync();
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Lock(string id, bool locked)
    {
        await Run(() => admin.SetLockedAsync(Uid, id, locked), locked ? "Customer locked out" : "Customer unlocked");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Freeze(string id, int accountId, bool frozen)
    {
        await Run(() => admin.SetFrozenAsync(Uid, accountId, frozen), frozen ? "Account frozen" : "Account unfrozen");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Adjust(string id, int accountId, decimal amount, string direction, string reason)
    {
        var signed = direction == "debit" ? -Math.Abs(amount) : Math.Abs(amount);
        await Run(() => admin.AdjustAsync(Uid, accountId, signed, reason), $"Adjustment of {Money.Signed(signed)} posted");
        return RedirectToAction(nameof(Details), new { id });
    }
}

public class LedgerController(ITransactionQueryService ledger) : AdminController
{
    /// <summary>Every transaction in the bank.</summary>
    public async Task<IActionResult> Index([FromQuery] TransactionQuery query)
    {
        var model = new TransactionResultsModel(await ledger.SearchAsync(query, null), query, nameof(Index), ShowAccount: true, ShowOwner: true);
        if (Request.Headers.ContainsKey("X-Partial")) return PartialView("_TransactionResults", model);
        return View(model);
    }

    /// <summary>Detail drawer for any transaction.</summary>
    public async Task<IActionResult> Details(long id, [FromServices] ApplicationDbContext db)
    {
        var tx = await db.Transactions.AsNoTracking().Include(t => t.BankAccount).ThenInclude(a => a.Owner).SingleOrDefaultAsync(t => t.Id == id);
        if (tx is null) return NotFound();
        ViewBag.Related = await db.Transactions.AsNoTracking().Include(t => t.BankAccount).FirstOrDefaultAsync(t => t.Reference == tx.Reference && t.Id != tx.Id);
        return PartialView("_TransactionDetails", tx);
    }
}

public class AuditController(ApplicationDbContext db) : AdminController
{
    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        const int size = 25;
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var p = "%" + q.Trim() + "%";
            query = query.Where(a => EF.Functions.Like(a.Action, p) || EF.Functions.Like(a.Details, p) || EF.Functions.Like(a.ActorName!, p));
        }
        var total = await query.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
        var items = await query.OrderByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        ViewBag.Q = q;
        return View(new PagedResult<AuditLog>(items, page, size, total));
    }
}

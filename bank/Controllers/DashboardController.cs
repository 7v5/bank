using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

public class DashboardController(ApplicationDbContext db, IInsightsService insights, IMoneyRequestService requests,
    IBudgetService budgets, IScheduledPaymentService schedules) : CustomerController
{
    public async Task<IActionResult> Index()
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Uid);
        var accounts = await db.BankAccounts.AsNoTracking()
            .Where(a => a.OwnerId == Uid && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Type).ThenBy(a => a.Id)
            .ToListAsync();
        var recent = await db.Transactions.AsNoTracking().Include(t => t.BankAccount)
            .Where(t => t.BankAccount.OwnerId == Uid)
            .OrderByDescending(t => t.PostedUtc).ThenByDescending(t => t.Id)
            .Take(8).ToListAsync();
        var month = (await insights.CashflowAsync(Uid, 1)).Single();
        var history = await insights.BalanceHistoryAsync(Uid, 30);

        return View(new DashboardViewModel
        {
            User = user,
            Accounts = accounts,
            Recent = recent,
            PendingIncoming = (await requests.IncomingAsync(Uid, pendingOnly: true)).Take(3).ToList(),
            MonthIn = month.MoneyIn,
            MonthOut = month.MoneyOut,
            BalanceChange30d = history.Count > 1 ? history[^1].Balance - history[0].Balance : null,
            Budgets = await budgets.StatusAsync(Uid),
            Upcoming = await schedules.ListAsync(Uid, activeOnly: true, take: 4)
        });
    }
}

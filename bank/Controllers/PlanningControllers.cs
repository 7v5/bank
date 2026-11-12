using Microsoft.AspNetCore.Mvc;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Controllers;

/// <summary>Payments set up for a future date: see them, pause, resume or cancel.</summary>
public class ScheduledController(IScheduledPaymentService schedules) : CustomerController
{
    public async Task<IActionResult> Index() => View(await schedules.ListAsync(Uid));

    [HttpPost]
    public Task<IActionResult> Pause(int id) => Change(() => schedules.PauseAsync(Uid, id), "Payment paused", "Nothing will be paid until you resume it.");

    [HttpPost]
    public Task<IActionResult> Resume(int id) => Change(() => schedules.ResumeAsync(Uid, id), "Payment resumed", "It will be paid on its next date.");

    [HttpPost]
    public Task<IActionResult> Cancel(int id) => Change(() => schedules.CancelAsync(Uid, id), "Payment cancelled", null);

    private async Task<IActionResult> Change(Func<Task> change, string title, string? body)
    {
        if (await TryBankAsync(change)) Flash(title, body);
        else FlashError("Couldn't change that payment", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Monthly spending limits by category.</summary>
public class BudgetsController(IBudgetService budgets) : CustomerController
{
    public async Task<IActionResult> Index() => View(await budgets.StatusAsync(Uid));

    [HttpPost]
    public async Task<IActionResult> Set(TransactionCategory category, decimal? monthlyLimit)
    {
        if (monthlyLimit is null) FlashError("Budget not saved", "Enter a monthly limit.");
        else if (await TryBankAsync(() => budgets.SetAsync(Uid, category, monthlyLimit.Value))) Flash($"{category} budget saved", $"{Money.Format(monthlyLimit.Value)} a month.");
        else FlashError("Budget not saved", ModelState[""]?.Errors.FirstOrDefault()?.ErrorMessage);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(int id)
    {
        await budgets.RemoveAsync(Uid, id);
        Flash("Budget removed");
        return RedirectToAction(nameof(Index));
    }
}

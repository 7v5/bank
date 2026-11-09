using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SimpleBankMvc.Data;
using SimpleBankMvc.Infrastructure;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Controllers;

/// <summary>
/// Base for pages that belong to a bank customer. Administrators are sent to the admin console,
/// and every action gets <see cref="Uid"/> for ownership-scoped queries.
/// </summary>
[Authorize]
public abstract class CustomerController : Controller
{
    protected string Uid => User.UserId();

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (User.IsInRole(DbInitializer.AdminRole))
            context.Result = RedirectToAction("Index", "Dashboard", new { area = "Admin" });
        base.OnActionExecuting(context);
    }

    /// <summary>Runs a banking operation; on a rule violation, records the message as a model error and returns false.</summary>
    protected async Task<bool> TryBankAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (BankingException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return false;
        }
    }

    protected void Flash(string title, string? body = null)
    {
        TempData["Success"] = title;
        if (body is not null) TempData["SuccessBody"] = body;
    }

    protected void FlashError(string title, string? body = null)
    {
        TempData["Error"] = title;
        if (body is not null) TempData["ErrorBody"] = body;
    }
}

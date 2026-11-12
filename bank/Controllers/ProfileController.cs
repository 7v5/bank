using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Infrastructure;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Controllers;

public class ProfileViewModel
{
    [Required(ErrorMessage = "Enter your full name."), StringLength(100, MinimumLength = 2), Display(Name = "Full name")] public string FullName { get; set; } = "";
    [Required, RegularExpression("^@?[A-Za-z0-9_]{3,20}$", ErrorMessage = "Use 3–20 letters, numbers or underscores.")] public string Handle { get; set; } = "";
    [RegularExpression("^(light|dark|system)$")] public string ThemePreference { get; set; } = "system";
    public string? Email { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Enter your current password."), DataType(DataType.Password), Display(Name = "Current password")] public string CurrentPassword { get; set; } = "";
    [Required(ErrorMessage = "Enter a new password."), StringLength(100, MinimumLength = 8), DataType(DataType.Password), Display(Name = "New password")] public string NewPassword { get; set; } = "";
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords don't match."), DataType(DataType.Password), Display(Name = "Confirm new password")] public string ConfirmPassword { get; set; } = "";
}

/// <summary>Profile and security settings. Available to customers and admins.</summary>
[Authorize]
public class ProfileController(ApplicationDbContext db, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditService audit,
    INotificationService notifications) : Controller
{
    private string Uid => User.UserId();

    /// <summary>Everything the page shows besides the details form: password form, security events and two-step state.</summary>
    private async Task FillSecurityAsync()
    {
        ViewBag.Password = new ChangePasswordViewModel();
        ViewBag.Events = await audit.SecurityEventsAsync(Uid, 8);
        var user = await users.GetUserAsync(User);
        ViewBag.TwoStepOn = user?.TwoFactorEnabled == true;
        // The setup key is only shown while setup is in progress, and recovery codes only once.
        if (user is not null && TempData["TwoStepSetup"] is not null)
        {
            var key = await users.GetAuthenticatorKeyAsync(user) ?? "";
            ViewBag.SetupKey = string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLowerInvariant();
        }
        ViewBag.RecoveryCodes = (TempData["RecoveryCodes"] as string)?.Split(',');
    }

    [HttpPost]
    public async Task<IActionResult> StartTwoStep()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!user.TwoFactorEnabled)
        {
            await users.ResetAuthenticatorKeyAsync(user); // also refreshes the security stamp
            await signIn.RefreshSignInAsync(user);
            TempData["TwoStepSetup"] = "1";
        }
        return Redirect(Url.Action(nameof(Index)) + "#security");
    }

    [HttpPost]
    public async Task<IActionResult> ConfirmTwoStep(string? code)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var clean = (code ?? "").Replace(" ", "").Replace("-", "");
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean))
        {
            TempData["TwoStepSetup"] = "1";
            TempData["Error"] = "That code didn't match";
            TempData["ErrorBody"] = "Enter the newest 6-digit code from your authenticator app.";
            return Redirect(Url.Action(nameof(Index)) + "#security");
        }
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await signIn.RefreshSignInAsync(user);
        await audit.LogAsync(user.Id, "security.two-step-on", "User", user.Id, "Turned on two-step sign-in");
        notifications.Add(user.Id, "Two-step sign-in is on", "You'll be asked for a code from your authenticator app each time you sign in.", "shield", "/Profile#security");
        await db.SaveChangesAsync();
        TempData["RecoveryCodes"] = string.Join(",", codes ?? []);
        TempData["Success"] = "Two-step sign-in is on";
        return Redirect(Url.Action(nameof(Index)) + "#security");
    }

    [HttpPost]
    public async Task<IActionResult> StopTwoStep()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        await audit.LogAsync(user.Id, "security.two-step-off", "User", user.Id, "Turned off two-step sign-in");
        notifications.Add(user.Id, "Two-step sign-in is off", "If you didn't turn it off, change your password and turn it back on.", "shield", "/Profile#security");
        await db.SaveChangesAsync();
        TempData["Success"] = "Two-step sign-in is off";
        return Redirect(Url.Action(nameof(Index)) + "#security");
    }

    public async Task<IActionResult> Index()
    {
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == Uid);
        await FillSecurityAsync();
        return View(new ProfileViewModel { FullName = u.FullName, Handle = u.Handle, ThemePreference = u.ThemePreference, Email = u.Email, CreatedUtc = u.CreatedUtc });
    }

    [HttpPost]
    public async Task<IActionResult> Index(ProfileViewModel vm)
    {
        var handle = vm.Handle.Trim().TrimStart('@').ToLowerInvariant();
        if (ModelState.IsValid && await db.Users.AnyAsync(u => u.Handle == handle && u.Id != Uid))
            ModelState.AddModelError(nameof(vm.Handle), "That handle is taken.");
        if (!ModelState.IsValid)
        {
            await FillSecurityAsync();
            vm.Email = User.Identity?.Name;
            return View(vm);
        }
        var user = await db.Users.SingleAsync(u => u.Id == Uid);
        user.FullName = vm.FullName.Trim();
        user.Handle = handle;
        user.ThemePreference = vm.ThemePreference;
        await db.SaveChangesAsync();
        TempData["Success"] = "Profile saved";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ChangePassword([Bind(Prefix = "Password")] ChangePasswordViewModel vm)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (ModelState.IsValid)
        {
            var result = await users.ChangePasswordAsync(user, vm.CurrentPassword, vm.NewPassword);
            if (result.Succeeded)
            {
                await signIn.RefreshSignInAsync(user);
                await audit.LogAsync(user.Id, "auth.password-changed", "User", user.Id, "Changed password");
                notifications.Add(user.Id, "Your password was changed", "If you didn't change it, contact us right away.", "shield", "/Profile#security");
                await db.SaveChangesAsync();
                TempData["Success"] = "Password changed";
                return Redirect(Url.Action(nameof(Index)) + "#security");
            }
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
        }
        TempData["Error"] = "Password not changed";
        TempData["ErrorBody"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        return Redirect(Url.Action(nameof(Index)) + "#security");
    }
}

/// <summary>The full notifications list.</summary>
[Authorize]
public class NotificationsController(INotificationService notifications, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index(string? tab)
    {
        ViewBag.Now = clock.GetUtcNow().UtcDateTime;
        var all = await notifications.RecentAsync(User.UserId(), 100);
        ViewBag.Tab = tab == "unread" ? "unread" : "all";
        ViewBag.UnreadCount = all.Count(n => !n.IsRead);
        return View(tab == "unread" ? all.Where(n => !n.IsRead).ToList() : all);
    }

    [HttpPost]
    public async Task<IActionResult> Read(int id, string? tab)
    {
        await notifications.MarkReadAsync(User.UserId(), id);
        return RedirectToAction(nameof(Index), new { tab });
    }

    [HttpPost]
    public async Task<IActionResult> ReadAll()
    {
        await notifications.MarkAllReadAsync(User.UserId());
        return RedirectToAction(nameof(Index));
    }
}

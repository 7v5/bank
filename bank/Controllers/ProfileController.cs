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
public class ProfileController(ApplicationDbContext db, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditService audit) : Controller
{
    private string Uid => User.UserId();

    public async Task<IActionResult> Index()
    {
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == Uid);
        ViewBag.Password = new ChangePasswordViewModel();
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
            ViewBag.Password = new ChangePasswordViewModel();
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
                TempData["Success"] = "Password changed";
                return RedirectToAction(nameof(Index));
            }
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
        }
        TempData["Error"] = "Password not changed";
        TempData["ErrorBody"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>The full notifications list.</summary>
[Authorize]
public class NotificationsController(INotificationService notifications, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index()
    {
        ViewBag.Now = clock.GetUtcNow().UtcDateTime;
        return View(await notifications.RecentAsync(User.UserId(), 100));
    }

    [HttpPost]
    public async Task<IActionResult> ReadAll()
    {
        await notifications.MarkAllReadAsync(User.UserId());
        return RedirectToAction(nameof(Index));
    }
}

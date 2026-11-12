using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;
using SimpleBankMvc.ViewModels;

namespace SimpleBankMvc.Controllers;

/// <summary>Sign in, register and sign out. Replaces the default Identity UI so the pages match the app.</summary>
public class AccountController(
    SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users,
    ApplicationDbContext db,
    IBankingService banking,
    INotificationService notifications,
    IAuditService audit) : Controller
{
    private static readonly string[] AvatarColors = ["#5b5bd6", "#e5484d", "#12a594", "#f76b15", "#8e4ec6", "#0d74ce", "#d6409f", "#2f8f3a"];

    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var result = await signIn.PasswordSignInAsync(vm.Email.Trim(), vm.Password, vm.RememberMe, lockoutOnFailure: true);
        var user = await users.FindByEmailAsync(vm.Email.Trim());
        if (result.Succeeded && user is not null) return await FinishSignInAsync(user, vm.ReturnUrl);
        // Password was right, but this customer turned on two-step sign-in: ask for the code next.
        if (result.RequiresTwoFactor) return RedirectToAction(nameof(LoginCode), new { returnUrl = vm.ReturnUrl, rememberMe = vm.RememberMe });

        if (result.IsLockedOut)
        {
            if (user is not null) await audit.LogAsync(user.Id, "auth.lockout", "User", user.Id, "Too many failed sign-in attempts");
            ModelState.AddModelError("", "This account is locked after too many attempts. Try again in a few minutes.");
        }
        else
        {
            ModelState.AddModelError("", "That email and password don't match.");
        }
        return View(vm);
    }

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> LoginCode(string? returnUrl = null, bool rememberMe = false)
    {
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is null) return RedirectToAction(nameof(Login));
        return View(new LoginCodeViewModel { ReturnUrl = returnUrl, RememberMe = rememberMe });
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> LoginCode(LoginCodeViewModel vm)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToAction(nameof(Login));
        if (!ModelState.IsValid) return View(vm);

        var code = vm.Code.Replace(" ", "").Replace("-", "");
        // Six digits is an authenticator code; anything else is treated as a one-time recovery code.
        var result = code.Length == 6 && code.All(char.IsDigit)
            ? await signIn.TwoFactorAuthenticatorSignInAsync(code, vm.RememberMe, rememberClient: false)
            : await signIn.TwoFactorRecoveryCodeSignInAsync(code);
        if (result.Succeeded) return await FinishSignInAsync(user, vm.ReturnUrl);

        if (result.IsLockedOut)
        {
            await audit.LogAsync(user.Id, "auth.lockout", "User", user.Id, "Too many failed sign-in codes");
            ModelState.AddModelError("", "This account is locked after too many attempts. Try again in a few minutes.");
        }
        else
        {
            ModelState.AddModelError("", "That code didn't work. Codes change every 30 seconds, so try the newest one.");
        }
        return View(vm);
    }

    /// <summary>Records the sign-in, warns the customer if it came from a new address, and sends them to the right home page.</summary>
    private async Task<IActionResult> FinishSignInAsync(ApplicationUser user, string? returnUrl)
    {
        var newAddress = await audit.IsNewSignInAddressAsync(user.Id);
        await audit.LogAsync(user.Id, "auth.login", "User", user.Id, "Signed in");
        if (newAddress)
        {
            notifications.Add(user.Id, "New sign-in to your account",
                "You signed in from an address we haven't seen before. If that wasn't you, change your password.", "shield", "/Profile#security");
            await db.SaveChangesAsync();
        }
        if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return await users.IsInRoleAsync(user, DbInitializer.AdminRole)
            ? RedirectToAction("Index", "Dashboard", new { area = "Admin" })
            : RedirectToAction("Index", "Dashboard");
    }

    [HttpGet, AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new RegisterViewModel());
    }

    [HttpPost, AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Register(RegisterViewModel vm)
    {
        var handle = vm.Handle.Trim().TrimStart('@').ToLowerInvariant();
        if (ModelState.IsValid && await db.Users.AnyAsync(u => u.Handle == handle))
            ModelState.AddModelError(nameof(vm.Handle), "That handle is taken.");
        if (!ModelState.IsValid) return View(vm);

        var user = new ApplicationUser
        {
            UserName = vm.Email.Trim(), Email = vm.Email.Trim(), FullName = vm.FullName.Trim(), Handle = handle,
            AvatarColor = AvatarColors[Random.Shared.Next(AvatarColors.Length)]
        };
        var result = await users.CreateAsync(user, vm.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code.Contains("Password") ? nameof(vm.Password) : e.Code.Contains("Email") || e.Code.Contains("UserName") ? nameof(vm.Email) : "", e.Description);
            return View(vm);
        }
        await users.AddToRoleAsync(user, DbInitializer.CustomerRole);

        // Every new customer starts with a primary checking account.
        await banking.OpenAccountAsync(user.Id, "Everyday Checking", AccountType.Checking);
        notifications.Add(user.Id, "Welcome to SimpleBank", "Your checking account is open. Make a deposit to get started.", "sparkles", "/Money/Deposit");
        await db.SaveChangesAsync();
        await audit.LogAsync(user.Id, "auth.register", "User", user.Id, $"Registered as @{handle}");

        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Success"] = $"Welcome, {user.FirstName}";
        TempData["SuccessBody"] = "Your checking account is ready.";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        TempData["Info"] = "You've been signed out.";
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();
}

using System.ComponentModel.DataAnnotations;

namespace SimpleBankMvc.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "Enter your email."), EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Enter your password."), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class LoginCodeViewModel
{
    [Required(ErrorMessage = "Enter the code from your authenticator app.")]
    public string Code { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required(ErrorMessage = "Enter your full name."), StringLength(100, MinimumLength = 2), Display(Name = "Full name")]
    public string FullName { get; set; } = "";

    [Required(ErrorMessage = "Pick a handle."), RegularExpression("^@?[A-Za-z0-9_]{3,20}$", ErrorMessage = "Use 3–20 letters, numbers or underscores.")]
    [Display(Name = "Handle")]
    public string Handle { get; set; } = "";

    [Required(ErrorMessage = "Enter your email."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Create a password."), StringLength(100, MinimumLength = 8, ErrorMessage = "Use at least 8 characters."), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Confirm your password."), Compare(nameof(Password), ErrorMessage = "Passwords don't match."), DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = "";
}

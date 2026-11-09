using System.Globalization;

namespace SimpleBankMvc.Services;

public static class Money
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    public static string Format(decimal amount) => amount.ToString("C", Culture);
    public static string Signed(decimal amount) => (amount >= 0 ? "+" : "−") + Math.Abs(amount).ToString("C", Culture);
}

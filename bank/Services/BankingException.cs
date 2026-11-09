namespace SimpleBankMvc.Services;

/// <summary>A business-rule violation whose message is safe to show to the user.</summary>
public class BankingException(string message) : Exception(message);

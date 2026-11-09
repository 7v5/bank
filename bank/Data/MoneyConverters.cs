using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SimpleBankMvc.Data;

/// <summary>
/// SQLite has no decimal type, and EF Core cannot compare or ORDER BY decimals stored as TEXT.
/// All money is therefore stored as whole cents in an INTEGER column, so comparisons and sorting run in SQL.
/// Sums are done in memory after a user/date-scoped query (see InsightsService).
/// </summary>
public sealed class MoneyToCentsConverter() : ValueConverter<decimal, long>(
    v => (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero),
    v => v / 100m);

public sealed class NullableMoneyToCentsConverter() : ValueConverter<decimal?, long?>(
    v => v.HasValue ? (long)Math.Round(v.Value * 100m, MidpointRounding.AwayFromZero) : null,
    v => v.HasValue ? v.Value / 100m : null);

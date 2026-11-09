using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public int FirstItem => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => Math.Min(Page * PageSize, TotalCount);
}

/// <summary>Filter, sort and paging options for transaction history. Bound from the query string.</summary>
public class TransactionQuery
{
    public static readonly int[] PageSizes = [10, 25, 50];

    public int? AccountId { get; set; }
    public TransactionType? Type { get; set; }
    public TransactionCategory? Category { get; set; }
    /// <summary>"in" = money in, "out" = money out, otherwise both.</summary>
    public string? Flow { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Q { get; set; }
    /// <summary>date_desc (default), date_asc, amount_desc, amount_asc.</summary>
    public string? Sort { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;

    public bool HasFilters => AccountId is not null || Type is not null || Category is not null || !string.IsNullOrEmpty(Flow)
                              || From is not null || To is not null || !string.IsNullOrWhiteSpace(Q);

    public TransactionQuery Normalize()
    {
        Page = Math.Max(1, Page);
        if (!PageSizes.Contains(PageSize)) PageSize = PageSizes[0];
        if (From is not null && To is not null && From > To) (From, To) = (To, From);
        Q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim()[..Math.Min(Q.Trim().Length, 100)];
        return this;
    }

    /// <summary>Route values for building pager / sort links that keep the current filters.</summary>
    public Dictionary<string, string> ToRouteValues(int? page = null, string? sort = null)
    {
        var d = new Dictionary<string, string>();
        if (AccountId is not null) d["accountId"] = AccountId.Value.ToString();
        if (Type is not null) d["type"] = Type.Value.ToString();
        if (Category is not null) d["category"] = Category.Value.ToString();
        if (!string.IsNullOrEmpty(Flow)) d["flow"] = Flow;
        if (From is not null) d["from"] = From.Value.ToString("yyyy-MM-dd");
        if (To is not null) d["to"] = To.Value.ToString("yyyy-MM-dd");
        if (Q is not null) d["q"] = Q;
        var s = sort ?? Sort;
        if (!string.IsNullOrEmpty(s) && s != "date_desc") d["sort"] = s;
        if (PageSize != PageSizes[0]) d["pageSize"] = PageSize.ToString();
        var p = page ?? Page;
        if (p > 1) d["page"] = p.ToString();
        return d;
    }
}

public interface ITransactionQueryService
{
    /// <summary>Searches the ledger. When <paramref name="ownerId"/> is null (admin), all users' transactions are searched.</summary>
    Task<PagedResult<Transaction>> SearchAsync(TransactionQuery query, string? ownerId);
}

public class TransactionQueryService(ApplicationDbContext db) : ITransactionQueryService
{
    public async Task<PagedResult<Transaction>> SearchAsync(TransactionQuery query, string? ownerId)
    {
        query.Normalize();
        var q = db.Transactions.AsNoTracking().Include(t => t.BankAccount).ThenInclude(a => a.Owner).AsQueryable();

        if (ownerId is not null) q = q.Where(t => t.BankAccount.OwnerId == ownerId);
        if (query.AccountId is { } accountId) q = q.Where(t => t.BankAccountId == accountId);
        if (query.Type is { } type) q = q.Where(t => t.Type == type);
        if (query.Category is { } category) q = q.Where(t => t.Category == category);
        if (query.Flow == "in") q = q.Where(t => t.Amount > 0);
        else if (query.Flow == "out") q = q.Where(t => t.Amount < 0);
        if (query.From is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            q = q.Where(t => t.PostedUtc >= start);
        }
        if (query.To is { } to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            q = q.Where(t => t.PostedUtc < end);
        }
        if (query.Q is { } text)
        {
            // LIKE is case-insensitive in SQLite (string.Contains would translate to case-sensitive instr()).
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(t => EF.Functions.Like(t.Description, pattern, "\\")
                             || EF.Functions.Like(t.CounterpartyName!, pattern, "\\")
                             || EF.Functions.Like(t.Reference, pattern, "\\")
                             || (ownerId == null && EF.Functions.Like(t.BankAccount.Owner.FullName, pattern, "\\")));
        }

        // Amount is stored as integer cents, so ORDER BY Amount runs in SQL.
        q = query.Sort switch
        {
            "date_asc" => q.OrderBy(t => t.PostedUtc).ThenBy(t => t.Id),
            "amount_desc" => q.OrderByDescending(t => t.Amount).ThenByDescending(t => t.Id),
            "amount_asc" => q.OrderBy(t => t.Amount).ThenByDescending(t => t.Id),
            _ => q.OrderByDescending(t => t.PostedUtc).ThenByDescending(t => t.Id)
        };

        var total = await q.CountAsync();
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)query.PageSize));
        if (query.Page > pages) query.Page = pages;
        var items = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return new PagedResult<Transaction>(items, query.Page, query.PageSize, total);
    }
}

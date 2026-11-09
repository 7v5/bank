using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Dtos;
using SimpleBankMvc.Infrastructure;
using SimpleBankMvc.Models;
using SimpleBankMvc.Services;

namespace SimpleBankMvc.Controllers.Api;

/// <summary>Turns business-rule violations into RFC 7807 400 responses.</summary>
public sealed class BankingExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not BankingException ex) return;
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest, Title = "The request couldn't be completed.", Detail = ex.Message
        }) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}

/// <summary>
/// Base for the JSON API. Uses the same auth cookie as the site; unsafe methods need the
/// antiforgery token in the RequestVerificationToken header (GET /api/v1/antiforgery/token).
/// </summary>
[ApiController, Authorize, Produces("application/json"), TypeFilter<BankingExceptionFilter>]
public abstract class ApiBase : ControllerBase
{
    protected string Uid => User.UserId();
}

[Route("api/v1/accounts"), Tags("Accounts")]
public class AccountsApiController(ApplicationDbContext db) : ApiBase
{
    /// <summary>Your open accounts, primary first.</summary>
    [HttpGet]
    public async Task<ListDto<AccountDto>> List() =>
        new((await db.BankAccounts.AsNoTracking().Where(a => a.OwnerId == Uid && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Type).ThenBy(a => a.Id).ToListAsync()).Select(AccountDto.From).ToList());

    /// <summary>One of your accounts.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<AccountDto>(200), ProducesResponseType(404)]
    public async Task<IActionResult> Get(int id)
    {
        var a = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == Uid);
        return a is null ? NotFound() : Ok(AccountDto.From(a));
    }
}

[Route("api/v1/transactions"), Tags("Transactions")]
public class TransactionsApiController(ITransactionQueryService ledger) : ApiBase
{
    /// <summary>Search your transactions. Same filters as the Activity page; paged.</summary>
    [HttpGet]
    public async Task<PagedDto<TransactionDto>> Search([FromQuery] TransactionQuery query)
    {
        var r = await ledger.SearchAsync(query, Uid);
        return new PagedDto<TransactionDto>(r.Items.Select(TransactionDto.From).ToList(), r.Page, r.PageSize, r.TotalCount, r.TotalPages);
    }
}

[Tags("Money movement")]
public class MoneyApiController(IBankingService banking) : ApiBase
{
    /// <summary>Deposit into one of your accounts (max $10,000).</summary>
    [HttpPost("api/v1/deposits")]
    public async Task<PostingResultDto> Deposit(CashRequest body) => Dto(await banking.DepositAsync(Uid, body.AccountId, body.Amount, body.Memo, body.IdempotencyKey));

    /// <summary>Withdraw from one of your accounts (max $10,000, up to the balance).</summary>
    [HttpPost("api/v1/withdrawals")]
    public async Task<PostingResultDto> Withdraw(CashRequest body) => Dto(await banking.WithdrawAsync(Uid, body.AccountId, body.Amount, body.Memo, body.IdempotencyKey));

    /// <summary>Move money between two of your own accounts.</summary>
    [HttpPost("api/v1/transfers")]
    public async Task<PostingResultDto> Transfer(TransferRequest body) =>
        Dto(await banking.TransferAsync(Uid, body.FromAccountId, body.ToAccountId, body.Amount, body.Memo, body.IdempotencyKey));

    /// <summary>Send money to another customer by @handle or email. Lands in their primary account.</summary>
    [HttpPost("api/v1/payments/send")]
    public async Task<PostingResultDto> Send(SendRequest body) =>
        Dto(await banking.SendToUserAsync(Uid, body.FromAccountId, body.Recipient, body.Amount, body.Note, body.IdempotencyKey));

    private static PostingResultDto Dto(PostingResult r) => new(r.Reference, r.NewBalance);
}

[Route("api/v1/users"), Tags("People")]
public class UsersApiController(ApplicationDbContext db) : ApiBase
{
    /// <summary>Find people to pay by name, handle or exact email. Returns public info only. Rate-limited.</summary>
    [HttpGet("lookup"), EnableRateLimiting("lookup")]
    public async Task<ListDto<UserLookupDto>> Lookup([FromQuery] string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return new([]);
        var handle = q.TrimStart('@').ToLowerInvariant();
        var pattern = "%" + handle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var email = q.ToUpperInvariant();
        var adminIds = db.UserRoles.Where(ur => db.Roles.Any(r => r.Id == ur.RoleId && r.Name == DbInitializer.AdminRole)).Select(ur => ur.UserId);
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Id != Uid && !adminIds.Contains(u.Id))
            .Where(u => u.Handle.StartsWith(handle) || EF.Functions.Like(u.FullName, pattern, "\\") || u.NormalizedEmail == email)
            .OrderBy(u => u.Handle == handle ? 0 : 1).ThenBy(u => u.FullName)
            .Take(8).ToListAsync();
        return new(users.Select(u => new UserLookupDto(u.Handle, u.FullName, u.Initials, u.AvatarColor)).ToList());
    }
}

[Route("api/v1/notifications"), Tags("Notifications")]
public class NotificationsApiController(INotificationService notifications, ApplicationDbContext db, TimeProvider clock) : ApiBase
{
    /// <summary>Your most recent notifications.</summary>
    [HttpGet]
    public async Task<ListDto<NotificationDto>> List([FromQuery] int take = 20)
    {
        var items = await notifications.RecentAsync(Uid, Math.Clamp(take, 1, 50));
        return new(items.Select(Dto).ToList());
    }

    /// <summary>Unread count, pending requests and the newest notification. Polled by the bell icon.</summary>
    [HttpGet("summary")]
    public async Task<NotificationSummaryDto> Summary()
    {
        var latest = (await notifications.RecentAsync(Uid, 1)).FirstOrDefault();
        var pending = await db.MoneyRequests.CountAsync(r => r.PayerId == Uid && r.Status == MoneyRequestStatus.Pending);
        return new(await notifications.UnreadCountAsync(Uid), pending, latest is null ? null : Dto(latest));
    }

    /// <summary>Mark one notification as read.</summary>
    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> Read(int id) { await notifications.MarkReadAsync(Uid, id); return NoContent(); }

    /// <summary>Mark all notifications as read.</summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll() { await notifications.MarkAllReadAsync(Uid); return NoContent(); }

    private NotificationDto Dto(Notification n) =>
        new(n.Id, n.Title, n.Body, n.Icon, n.Link, n.IsRead, n.CreatedUtc, Ui.Ago(n.CreatedUtc, clock.GetUtcNow().UtcDateTime));
}

[Route("api/v1/insights"), Tags("Insights")]
public class InsightsApiController(IInsightsService insights, TimeProvider clock) : ApiBase
{
    /// <summary>Your total balance at the end of each day (7–365 days).</summary>
    [HttpGet("balance-history")]
    public async Task<ListDto<BalancePointDto>> BalanceHistory([FromQuery] int days = 30) =>
        new((await insights.BalanceHistoryAsync(Uid, days)).Select(p => new BalancePointDto(p.Date, p.Balance)).ToList());

    /// <summary>Spending by category for a month (yyyy-MM, default this month). Excludes transfers between your own accounts.</summary>
    [HttpGet("spending")]
    public async Task<ListDto<CategoryTotalDto>> Spending([FromQuery] string? month = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var (y, m) = (now.Year, now.Month);
        if (month is not null && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var d)) (y, m) = (d.Year, d.Month);
        return new((await insights.SpendingByCategoryAsync(Uid, y, m)).Select(c => new CategoryTotalDto(c.Category, c.Amount, c.Count)).ToList());
    }

    /// <summary>Money in vs. money out per month for the last N months (1–24).</summary>
    [HttpGet("cashflow")]
    public async Task<ListDto<MonthFlowDto>> Cashflow([FromQuery] int months = 6) =>
        new((await insights.CashflowAsync(Uid, months)).Select(f => new MonthFlowDto(f.Label, f.Year, f.Month, f.MoneyIn, f.MoneyOut, f.Net)).ToList());
}

[Route("api/v1/profile"), Tags("Profile")]
public class ProfileApiController(ApplicationDbContext db) : ApiBase
{
    /// <summary>Save your light/dark theme preference.</summary>
    [HttpPost("theme")]
    public async Task<IActionResult> Theme(ThemeRequest body)
    {
        await db.Users.Where(u => u.Id == Uid).ExecuteUpdateAsync(s => s.SetProperty(u => u.ThemePreference, body.Theme));
        return NoContent();
    }
}

[Route("api/v1/antiforgery"), Tags("Security")]
public class AntiforgeryApiController(IAntiforgery antiforgery) : ApiBase
{
    /// <summary>
    /// Returns a token for the RequestVerificationToken header. Needed for POST requests
    /// (for example when trying them from the API reference page).
    /// </summary>
    [HttpGet("token")]
    public AntiforgeryTokenDto Token()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new AntiforgeryTokenDto(tokens.HeaderName!, tokens.RequestToken!);
    }
}

[Route("api/v1/admin"), Tags("Admin"), Authorize(Roles = DbInitializer.AdminRole)]
public class AdminApiController(IAdminService admin) : ApiBase
{
    /// <summary>Bank-wide KPIs and 30-day volume. Admin role only.</summary>
    [HttpGet("stats")]
    public async Task<object> Stats()
    {
        var s = await admin.StatsAsync();
        return new
        {
            s.Customers, s.NewCustomers30d, s.OpenAccounts, s.TotalDeposits, s.Volume30d, s.Transactions30d, s.PendingRequests, s.FrozenAccounts,
            DailyVolume = s.DailyVolume.Select(d => new { Date = d.Day, d.Volume, d.Count }),
            ByAccountType = s.ByType.Select(t => new { t.Type, t.Balance, t.Count })
        };
    }
}

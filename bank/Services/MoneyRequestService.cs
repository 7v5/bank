using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public interface IMoneyRequestService
{
    Task<MoneyRequest> CreateAsync(string requesterId, string payer, decimal amount, string? note);
    Task CancelAsync(string requesterId, int requestId);
    Task DeclineAsync(string payerId, int requestId);
    Task<PostingResult> PayAsync(string payerId, int requestId, int fromAccountId, Guid? idempotencyKey = null);
    Task<List<MoneyRequest>> IncomingAsync(string userId, bool pendingOnly = false);
    Task<List<MoneyRequest>> OutgoingAsync(string userId);
}

/// <summary>
/// Request lifecycle: Pending → Paid | Declined | Cancelled. Only pending requests can change state.
/// Paying reuses BankingService.SendCoreAsync inside this service's own transaction, so the money
/// movement and the request status update commit (or roll back) together.
/// </summary>
public class MoneyRequestService(ApplicationDbContext db, BankingService banking, INotificationService notifications, TimeProvider clock) : IMoneyRequestService
{
    public const int MaxPendingPerRequester = 20;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<MoneyRequest> CreateAsync(string requesterId, string payer, decimal amount, string? note)
    {
        if (amount <= 0) throw new BankingException("Amount must be greater than zero.");
        if (decimal.Round(amount, 2) != amount) throw new BankingException("Amounts can have at most two decimal places.");
        if (amount > BankingService.DailySendLimit) throw new BankingException($"You can request at most {Money.Format(BankingService.DailySendLimit)}.");

        var target = await banking.FindRecipientAsync(payer) ?? throw new BankingException("We couldn't find anyone with that handle or email.");
        if (target.Id == requesterId) throw new BankingException("You can't request money from yourself.");
        var pending = await db.MoneyRequests.CountAsync(r => r.RequesterId == requesterId && r.Status == MoneyRequestStatus.Pending);
        if (pending >= MaxPendingPerRequester) throw new BankingException("You have too many pending requests. Cancel some first.");

        var requester = await db.Users.SingleAsync(u => u.Id == requesterId);
        var request = new MoneyRequest
        {
            RequesterId = requesterId, PayerId = target.Id, Amount = amount,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedUtc = Now
        };
        db.MoneyRequests.Add(request);
        notifications.Add(target.Id, $"{requester.FullName} requested {Money.Format(amount)}",
            request.Note is null ? "Tap to pay or decline." : $"“{request.Note}”", "request", "/Requests");
        await db.SaveChangesAsync();
        request.Payer = target;
        return request;
    }

    public async Task CancelAsync(string requesterId, int requestId)
    {
        var request = await LoadPendingAsync(r => r.Id == requestId && r.RequesterId == requesterId);
        request.Status = MoneyRequestStatus.Cancelled;
        request.RespondedUtc = Now;
        await db.SaveChangesAsync();
    }

    public async Task DeclineAsync(string payerId, int requestId)
    {
        var request = await LoadPendingAsync(r => r.Id == requestId && r.PayerId == payerId);
        request.Status = MoneyRequestStatus.Declined;
        request.RespondedUtc = Now;
        var payer = await db.Users.SingleAsync(u => u.Id == payerId);
        notifications.Add(request.RequesterId, $"{payer.FullName} declined your request",
            $"Your request for {Money.Format(request.Amount)} was declined.", "x", "/Requests?tab=outgoing");
        await db.SaveChangesAsync();
    }

    public Task<PostingResult> PayAsync(string payerId, int requestId, int fromAccountId, Guid? idempotencyKey = null) =>
        db.InTransactionAsync(async () =>
        {
            if (idempotencyKey is { } key && key != Guid.Empty)
            {
                if (await db.ProcessedRequests.AnyAsync(p => p.Key == key)) throw new BankingException("This request was already submitted.");
                db.ProcessedRequests.Add(new ProcessedRequest { Key = key, UserId = payerId, CreatedUtc = Now });
            }
            var request = await LoadPendingAsync(r => r.Id == requestId && r.PayerId == payerId);
            var requester = await db.Users.SingleAsync(u => u.Id == request.RequesterId);
            var result = await banking.SendCoreAsync(payerId, fromAccountId, requester, request.Amount,
                request.Note ?? "Paid your request");
            request.Status = MoneyRequestStatus.Paid;
            request.RespondedUtc = Now;
            request.PaymentReference = result.Reference;
            return result;
        });

    public Task<List<MoneyRequest>> IncomingAsync(string userId, bool pendingOnly = false) =>
        db.MoneyRequests.AsNoTracking().Include(r => r.Requester)
            .Where(r => r.PayerId == userId && (!pendingOnly || r.Status == MoneyRequestStatus.Pending))
            .OrderByDescending(r => r.Status == MoneyRequestStatus.Pending).ThenByDescending(r => r.CreatedUtc).Take(100).ToListAsync();

    public Task<List<MoneyRequest>> OutgoingAsync(string userId) =>
        db.MoneyRequests.AsNoTracking().Include(r => r.Payer)
            .Where(r => r.RequesterId == userId)
            .OrderByDescending(r => r.Status == MoneyRequestStatus.Pending).ThenByDescending(r => r.CreatedUtc).Take(100).ToListAsync();

    private async Task<MoneyRequest> LoadPendingAsync(System.Linq.Expressions.Expression<Func<MoneyRequest, bool>> predicate)
    {
        var request = await db.MoneyRequests.SingleOrDefaultAsync(predicate) ?? throw new BankingException("Request was not found.");
        if (request.Status != MoneyRequestStatus.Pending) throw new BankingException($"This request is already {request.Status.ToString().ToLowerInvariant()}.");
        return request;
    }
}

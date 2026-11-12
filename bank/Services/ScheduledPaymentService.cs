using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public interface IScheduledPaymentService
{
    Task<ScheduledPayment> CreateAsync(string userId, ScheduleKind kind, int fromAccountId, int? toAccountId, int? payeeId,
        decimal amount, string? memo, DateOnly firstRun, ScheduleFrequency frequency);
    Task PauseAsync(string userId, int id);
    Task ResumeAsync(string userId, int id);
    Task CancelAsync(string userId, int id);
    Task<List<ScheduledPayment>> ListAsync(string userId, bool activeOnly = false, int? take = null);
    Task<int> RunDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Future-dated and repeating payments. Each run goes through <see cref="BankingService"/>, so the same rules
/// apply as when the customer pays by hand, and uses an idempotency key derived from the schedule and its run
/// date, so one occurrence can never post twice.
/// </summary>
public class ScheduledPaymentService(ApplicationDbContext db, BankingService banking, INotificationService notifications, TimeProvider clock)
    : IScheduledPaymentService
{
    public const int MaxActivePerUser = 20;
    /// <summary>How many missed occurrences one schedule catches up on in a single pass.</summary>
    private const int MaxCatchUp = 6;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<ScheduledPayment> CreateAsync(string userId, ScheduleKind kind, int fromAccountId, int? toAccountId, int? payeeId,
        decimal amount, string? memo, DateOnly firstRun, ScheduleFrequency frequency)
    {
        if (amount <= 0) throw new BankingException("Amount must be greater than zero.");
        if (decimal.Round(amount, 2) != amount) throw new BankingException("Amounts can have at most two decimal places.");
        if (amount > BankingService.MaxTransferAmount) throw new BankingException($"The maximum for this action is {Money.Format(BankingService.MaxTransferAmount)}.");
        if (firstRun <= Today) throw new BankingException("Choose a date after today.");
        if (firstRun > Today.AddYears(1)) throw new BankingException("Choose a date within the next year.");

        var from = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == fromAccountId && a.OwnerId == userId && a.IsActive)
            ?? throw new BankingException("Account was not found.");
        if (kind == ScheduleKind.Transfer)
        {
            if (toAccountId == fromAccountId) throw new BankingException("Choose two different accounts.");
            if (!await db.BankAccounts.AnyAsync(a => a.Id == toAccountId && a.OwnerId == userId && a.IsActive))
                throw new BankingException("Account was not found.");
            payeeId = null;
        }
        else
        {
            if (!await db.Payees.AnyAsync(p => p.Id == payeeId && p.OwnerId == userId)) throw new BankingException("Payee was not found.");
            toAccountId = null;
        }
        if (await db.ScheduledPayments.CountAsync(s => s.OwnerId == userId && s.Status == ScheduleStatus.Active) >= MaxActivePerUser)
            throw new BankingException($"You can have up to {MaxActivePerUser} scheduled payments at once.");

        var schedule = new ScheduledPayment
        {
            OwnerId = userId, Kind = kind, FromAccountId = from.Id, ToAccountId = toAccountId, PayeeId = payeeId,
            Amount = amount, Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim(),
            Frequency = frequency, StartDate = firstRun, NextRunDate = firstRun,
            CreatedUtc = clock.GetUtcNow().UtcDateTime
        };
        db.ScheduledPayments.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    public Task PauseAsync(string userId, int id) => ChangeAsync(userId, id, s =>
    {
        if (s.Status != ScheduleStatus.Active) throw new BankingException("Only an active payment can be paused.");
        s.Status = ScheduleStatus.Paused;
        s.LastResult = "Paused by you";
    });

    public Task ResumeAsync(string userId, int id) => ChangeAsync(userId, id, s =>
    {
        if (s.Status != ScheduleStatus.Paused) throw new BankingException("Only a paused payment can be resumed.");
        s.Status = ScheduleStatus.Active;
        s.LastResult = null;
        // Skip any dates missed while paused instead of paying them all at once.
        while (s.NextRunDate <= Today && s.Frequency != ScheduleFrequency.Once) Advance(s);
        if (s.NextRunDate <= Today) s.NextRunDate = Today.AddDays(1);
    });

    public Task CancelAsync(string userId, int id) => ChangeAsync(userId, id, s =>
    {
        if (s.Status is ScheduleStatus.Completed or ScheduleStatus.Cancelled) throw new BankingException("This payment has already ended.");
        s.Status = ScheduleStatus.Cancelled;
    });

    public Task<List<ScheduledPayment>> ListAsync(string userId, bool activeOnly = false, int? take = null)
    {
        var q = db.ScheduledPayments.AsNoTracking()
            .Include(s => s.FromAccount).Include(s => s.ToAccount).Include(s => s.Payee)
            .Where(s => s.OwnerId == userId);
        if (activeOnly) q = q.Where(s => s.Status == ScheduleStatus.Active);
        q = q.OrderBy(s => s.Status == ScheduleStatus.Active ? 0 : s.Status == ScheduleStatus.Paused ? 1 : 2).ThenBy(s => s.NextRunDate).ThenBy(s => s.Id);
        if (take is { } n) q = q.Take(n);
        return q.ToListAsync();
    }

    /// <summary>Makes every payment that is due today or earlier. Returns how many posted.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var today = Today;
        var dueIds = await db.ScheduledPayments.Where(s => s.Status == ScheduleStatus.Active && s.NextRunDate <= today)
            .OrderBy(s => s.NextRunDate).Select(s => s.Id).ToListAsync(ct);
        var posted = 0;
        foreach (var id in dueIds)
        {
            for (var i = 0; i < MaxCatchUp && !ct.IsCancellationRequested; i++)
            {
                var s = await db.ScheduledPayments.Include(x => x.Payee).Include(x => x.ToAccount).SingleAsync(x => x.Id == id, ct);
                if (s.Status != ScheduleStatus.Active || s.NextRunDate > today) break;
                var what = s.Kind == ScheduleKind.Bill ? $"payment to {s.Payee?.Name}" : $"transfer to {s.ToAccount?.Name}";
                var key = RunKey(s.Id, s.NextRunDate);
                try
                {
                    // If a crash interrupted an earlier pass after the money moved, don't move it again.
                    if (!await db.ProcessedRequests.AnyAsync(p => p.Key == key, ct))
                    {
                        if (s.Kind == ScheduleKind.Bill) await banking.PayBillAsync(s.OwnerId, s.FromAccountId, s.PayeeId ?? 0, s.Amount, s.Memo, key);
                        else await banking.TransferAsync(s.OwnerId, s.FromAccountId, s.ToAccountId ?? 0, s.Amount, s.Memo, key);
                        posted++;
                    }
                }
                catch (BankingException ex)
                {
                    // A failed banking call clears the change tracker, so load the schedule again before updating it.
                    s = await db.ScheduledPayments.SingleAsync(x => x.Id == id, ct);
                    s.Status = ScheduleStatus.Paused;
                    s.LastResult = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                    notifications.Add(s.OwnerId, $"Scheduled {what} didn't go through",
                        $"{ex.Message} We paused it. Resume it when you're ready.", "alert", "/Scheduled");
                    await db.SaveChangesAsync(ct);
                    break;
                }

                s.RunCount++;
                s.LastRunUtc = clock.GetUtcNow().UtcDateTime;
                s.LastResult = "Paid";
                if (s.Frequency == ScheduleFrequency.Once) s.Status = ScheduleStatus.Completed;
                else Advance(s);
                notifications.Add(s.OwnerId, $"Scheduled {what} sent", $"{Money.Format(s.Amount)} left your account as planned.",
                    s.Kind == ScheduleKind.Bill ? "receipt" : "swap", "/Scheduled");
                await db.SaveChangesAsync(ct);
            }
        }
        return posted;
    }

    /// <summary>Moves a repeating schedule to its next date. RunCount counts dates handled so far, paid or skipped.</summary>
    private static void Advance(ScheduledPayment s)
    {
        var steps = s.Frequency == ScheduleFrequency.Weekly
            ? (s.NextRunDate.DayNumber - s.StartDate.DayNumber) / 7 + 1
            : (s.NextRunDate.Year - s.StartDate.Year) * 12 + s.NextRunDate.Month - s.StartDate.Month + 1;
        s.NextRunDate = s.Frequency == ScheduleFrequency.Weekly ? s.StartDate.AddDays(7 * steps) : s.StartDate.AddMonths(steps);
    }

    private async Task ChangeAsync(string userId, int id, Action<ScheduledPayment> change)
    {
        var s = await db.ScheduledPayments.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == userId)
            ?? throw new BankingException("Scheduled payment was not found.");
        change(s);
        await db.SaveChangesAsync();
    }

    /// <summary>The same schedule and date always give the same key.</summary>
    public static Guid RunKey(int scheduleId, DateOnly date) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"scheduled:{scheduleId}:{date:yyyy-MM-dd}")));

    public static string FrequencyLabel(ScheduleFrequency f) => f switch
    {
        ScheduleFrequency.Weekly => "Every week",
        ScheduleFrequency.Monthly => "Every month",
        _ => "One time"
    };
}

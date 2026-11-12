using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public interface IAuditService
{
    Task LogAsync(string? actorId, string action, string targetType, string? targetId, string details);
    /// <summary>The user's own sign-in and security events, newest first.</summary>
    Task<List<AuditLog>> SecurityEventsAsync(string userId, int take = 10);
    /// <summary>True when the user has signed in before, but never from the address this request comes from.</summary>
    Task<bool> IsNewSignInAddressAsync(string userId);
}

/// <summary>Writes audit events to the AuditLogs table (and the application log).</summary>
public class DbAuditService(ApplicationDbContext db, IHttpContextAccessor http, TimeProvider clock, ILogger<DbAuditService> logger) : IAuditService
{
    public async Task LogAsync(string? actorId, string action, string targetType, string? targetId, string details)
    {
        var name = actorId is null ? null : await db.Users.Where(u => u.Id == actorId).Select(u => u.FullName).FirstOrDefaultAsync();
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId, ActorName = name, Action = action, TargetType = targetType, TargetId = targetId,
            Details = details.Length > 500 ? details[..500] : details,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            CreatedUtc = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
        logger.LogInformation("AUDIT {Action} by {Actor} on {TargetType}:{TargetId} — {Details}", action, actorId, targetType, targetId, details);
    }

    public Task<List<AuditLog>> SecurityEventsAsync(string userId, int take = 10) =>
        db.AuditLogs.AsNoTracking()
            .Where(a => a.ActorId == userId && (a.Action.StartsWith("auth.") || a.Action.StartsWith("security.")))
            .OrderByDescending(a => a.CreatedUtc).ThenByDescending(a => a.Id).Take(take).ToListAsync();

    public async Task<bool> IsNewSignInAddressAsync(string userId)
    {
        var ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        if (ip is null) return false;
        var seen = await db.AuditLogs.Where(a => a.ActorId == userId && a.Action == "auth.login").Select(a => a.IpAddress).Distinct().ToListAsync();
        return seen.Count > 0 && !seen.Contains(ip);
    }
}

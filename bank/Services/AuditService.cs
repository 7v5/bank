using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public interface IAuditService
{
    Task LogAsync(string? actorId, string action, string targetType, string? targetId, string details);
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
}

namespace SimpleBankMvc.Services;

public interface IAuditService
{
    Task LogAsync(string? actorId, string action, string targetType, string? targetId, string details);
}

/// <summary>Writes audit events to the application log. Replaced by a database-backed log in a later migration.</summary>
public class LoggerAuditService(ILogger<LoggerAuditService> logger) : IAuditService
{
    public Task LogAsync(string? actorId, string action, string targetType, string? targetId, string details)
    {
        logger.LogInformation("AUDIT {Action} by {Actor} on {TargetType}:{TargetId} — {Details}", action, actorId, targetType, targetId, details);
        return Task.CompletedTask;
    }
}

using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Infrastructure;

public record ShellModel(ApplicationUser User, bool IsAdmin, int UnreadNotifications, int PendingRequests, int LatestNotificationId);

/// <summary>
/// Loads, once per request, the signed-in user's data that the layout chrome needs
/// (name, avatar, badges). Injected into _Layout so controllers don't have to pass it.
/// </summary>
public class ShellContext(IHttpContextAccessor http, ApplicationDbContext db)
{
    private ShellModel? _cached;
    private bool _loaded;

    public async Task<ShellModel?> GetAsync()
    {
        if (_loaded) return _cached;
        _loaded = true;
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.UserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return null;
        var unread = await db.Notifications.CountAsync(n => n.UserId == id && !n.IsRead);
        var latest = await db.Notifications.Where(n => n.UserId == id).MaxAsync(n => (int?)n.Id) ?? 0;
        var pending = await db.MoneyRequests.CountAsync(r => r.PayerId == id && r.Status == MoneyRequestStatus.Pending);
        _cached = new ShellModel(user, principal.IsInRole(DbInitializer.AdminRole), unread, pending, latest);
        return _cached;
    }
}

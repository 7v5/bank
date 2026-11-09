using Microsoft.EntityFrameworkCore;
using SimpleBankMvc.Data;
using SimpleBankMvc.Models;

namespace SimpleBankMvc.Services;

public interface INotificationService
{
    /// <summary>Queues a notification on the current unit of work (the caller saves).</summary>
    void Add(string userId, string title, string body, string icon = "bell", string? link = null);
    Task<List<Notification>> RecentAsync(string userId, int take = 20);
    Task<int> UnreadCountAsync(string userId);
    Task MarkReadAsync(string userId, int id);
    Task MarkAllReadAsync(string userId);
}

public class NotificationService(ApplicationDbContext db, TimeProvider clock) : INotificationService
{
    public void Add(string userId, string title, string body, string icon = "bell", string? link = null) =>
        db.Notifications.Add(new Notification
        {
            UserId = userId, Title = title, Body = body, Icon = icon, Link = link,
            CreatedUtc = clock.GetUtcNow().UtcDateTime
        });

    public Task<List<Notification>> RecentAsync(string userId, int take = 20) =>
        db.Notifications.AsNoTracking().Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedUtc).ThenByDescending(n => n.Id).Take(take).ToListAsync();

    public Task<int> UnreadCountAsync(string userId) => db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    public Task MarkReadAsync(string userId, int id) =>
        db.Notifications.Where(n => n.UserId == userId && n.Id == id).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

    public Task MarkAllReadAsync(string userId) =>
        db.Notifications.Where(n => n.UserId == userId && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
}

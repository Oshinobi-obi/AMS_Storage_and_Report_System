// This is for the activity log (who did what and when) and the announcements.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed record ActivityRow(DateTime At, string User, string Role, string Type, string Description, string? Ip);

public sealed class ActivityFilter
{
    public string Search { get; set; } = "";
    public uint UserId { get; set; }
    public string Type { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed class AdminExtrasService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int PageSize = 100;

    // Activity log
    // One page of the activity log (newest first) and how many entries match the filters.
    public async Task<(List<ActivityRow> Rows, int Total)> ActivityAsync(ActivityFilter f, int page, int size = Paging.Size)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.UserActivities.AsNoTracking().AsQueryable();
        if (f.UserId != 0) q = q.Where(a => a.UserId == f.UserId);
        if (f.Type.Length > 0) q = q.Where(a => a.ActivityType == f.Type);
        if (f.From is { } from) q = q.Where(a => a.CreatedAt >= from.Date);
        if (f.To is { } to) q = q.Where(a => a.CreatedAt < to.Date.AddDays(1));
        var term = f.Search.Trim();
        if (term.Length > 0) q = q.Where(a => a.Description.Contains(term));

        var total = await q.CountAsync();
        var pages = Math.Max(1, (total + size - 1) / size);
        page = Math.Clamp(page, 1, pages);
        var raw = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ActivityId)
            .Skip((page - 1) * size).Take(size)
            .Select(a => new
            {
                a.CreatedAt, a.ActivityType, a.Description, a.IpAddress,
                DisplayName = a.User != null ? a.User.DisplayName : null,
                FullName = a.User != null ? a.User.FullName : null,
                Role = a.User != null ? (UserRole?)a.User.Role : null,
            })
            .ToListAsync();
        var rows = raw.Select(a => new ActivityRow(a.CreatedAt,
                string.IsNullOrWhiteSpace(a.DisplayName) ? a.FullName ?? "Deleted account" : a.DisplayName,
                a.Role switch { UserRole.SuperAdmin => "Super Admin", UserRole.Admin => "Admin", UserRole.Office => "Office", _ => "" },
                a.ActivityType, a.Description, a.IpAddress)).ToList();
        return (rows, total);
    }

    public async Task<List<string>> ActivityTypesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserActivities.AsNoTracking().Select(a => a.ActivityType).Distinct().OrderBy(t => t).ToListAsync();
    }

    public async Task<List<User>> UsersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // I sort after loading because Name (display name or full name) isn't a database column,
        // so the database can't sort by it.
        var users = await db.Users.AsNoTracking().ToListAsync();
        return users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Announcements
    public async Task<List<Announcement>> AnnouncementsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Announcements.AsNoTracking().OrderByDescending(a => a.StartsAt).ToListAsync();
    }

    public async Task<OpResult> SaveAnnouncementAsync(Announcement f, uint userId)
    {
        var title = f.Title.Trim();
        var message = f.Message.Trim();
        if (title.Length is 0 or > 150) return OpResult.Fail("Enter a short title (up to 150 characters).");
        if (message.Length is 0 or > 1000) return OpResult.Fail("Enter the message (up to 1,000 characters).");
        if (f.Level is not ("info" or "warning" or "urgent")) return OpResult.Fail("Choose how important it is.");
        if (f.EndsAt is { } end && end <= f.StartsAt) return OpResult.Fail("The end must be after the start.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var a = f.AnnouncementId == 0 ? new Announcement { CreatedBy = userId, CreatedAt = PhTime.Now } : await db.Announcements.FindAsync(f.AnnouncementId);
        if (a is null) return OpResult.Fail("This announcement no longer exists.");
        a.Title = title;
        a.Message = message;
        a.Level = f.Level;
        a.StartsAt = f.StartsAt;
        a.EndsAt = f.EndsAt;
        a.IsActive = f.IsActive;
        if (f.AnnouncementId == 0) db.Announcements.Add(a);
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = f.AnnouncementId == 0 ? "Create" : "Update", Description = $"{(f.AnnouncementId == 0 ? "Posted" : "Edited")} announcement \"{title}\"", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success(f.AnnouncementId == 0 ? "Announcement posted." : "Announcement saved.");
    }

    public async Task<OpResult> DeleteAnnouncementAsync(int id, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var a = await db.Announcements.FindAsync(id);
        if (a is null) return OpResult.Fail("This announcement no longer exists.");
        db.Announcements.Remove(a);
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Delete", Description = $"Deleted announcement \"{a.Title}\"", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success("Announcement deleted.");
    }
}

// This is for the birthdays used by My accountability in e-SRS (database update 019).
//
// People set their own birthday the first time they open My accountability. After that only AMS
// can change it, here in Employees. I only save a hash of the birthday (BirthdayHash), so nobody can
// read it back, not even AMS. AMS can only see if one is set, set a new one, remove it, or unlock
// someone who got locked out after 5 wrong tries.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed record BirthdayInfo(string SetBy, DateTime SetAtUtc, DateTime? LockedUntilUtc, DateTime? LastVerifiedUtc)
{
    public bool IsLocked => LockedUntilUtc is { } u && u > DateTime.UtcNow;
    public bool SetBySelf => SetBy == "self";
}

public sealed class PersonnelBirthdays(IDbContextFactory<AppDbContext> dbFactory, ILogger<PersonnelBirthdays> log)
{
    /// Birthday info by personnel id, null if database update 019 isn't run yet.
    public async Task<Dictionary<int, BirthdayInfo>?> AllAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await db.Database.SqlQuery<Row>($@"
                SELECT CAST(personnel_id AS SIGNED) AS PersonnelId, set_by AS SetBy, set_at AS SetAt,
                       locked_until AS LockedUntil, last_verified AS LastVerified
                  FROM personnel_birthdays").ToListAsync();
            return rows.ToDictionary(r => (int)r.PersonnelId, r => new BirthdayInfo(r.SetBy, r.SetAt, r.LockedUntil, r.LastVerified));
        }
        catch (Exception ex) when (ex.GetBaseException().Message.Contains("doesn't exist"))
        {
            log.LogWarning("personnel_birthdays table is missing. Run database update 019_personnel_birthdays.sql.");
            return null;
        }
    }

    /// AMS sets (or changes) a person's birthday.
    public async Task SetAsync(int personnelId, DateOnly birthday, uint userId, string userName)
    {
        var hash = BirthdayHash.Hash(personnelId, birthday);
        var by = $"AMS: {userName}";
        if (by.Length > 100) by = by[..100];
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO personnel_birthdays (personnel_id, birthday_hash, set_by, set_at, failed_tries, locked_until)
            VALUES ({personnelId}, {hash}, {by}, {DateTime.UtcNow}, 0, NULL)
            ON DUPLICATE KEY UPDATE birthday_hash = {hash}, set_by = {by}, set_at = {DateTime.UtcNow},
                                    failed_tries = 0, locked_until = NULL");
        await LogAsync(db, userId, $"Changed the My accountability birthday of {await NameAsync(db, personnelId)}");
    }

    /// Removes the birthday. The person sets a new one the next time they open My accountability.
    public async Task ClearAsync(int personnelId, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM personnel_birthdays WHERE personnel_id = {personnelId}");
        await LogAsync(db, userId, $"Removed the My accountability birthday of {await NameAsync(db, personnelId)}");
    }

    public async Task UnlockAsync(int personnelId, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE personnel_birthdays SET failed_tries = 0, locked_until = NULL WHERE personnel_id = {personnelId}");
        await LogAsync(db, userId, $"Unlocked My accountability of {await NameAsync(db, personnelId)}");
    }

    private static async Task<string> NameAsync(AppDbContext db, int personnelId) =>
        await db.RoPersonnel.Where(p => p.PersonnelId == personnelId).Select(p => p.FullName).FirstOrDefaultAsync() ?? $"personnel #{personnelId}";

    private async Task LogAsync(AppDbContext db, uint userId, string text)
    {
        if (userId == 0) return;
        try
        {
            db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = text, CreatedAt = PhTime.Now });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Activity log failed: {Text}", text); }
    }

    private sealed class Row
    {
        public long PersonnelId { get; set; }
        public string SetBy { get; set; } = "";
        public DateTime SetAt { get; set; }
        public DateTime? LockedUntil { get; set; }
        public DateTime? LastVerified { get; set; }
    }
}

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public class AuthResult
{
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public User? User { get; set; }
}

public class AuthService(IDbContextFactory<AppDbContext> dbFactory)
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const int TargetBcryptCost = 12;
    private const string GenericError = "Incorrect username or password.";

    public async Task<AuthResult> ValidateLoginAsync(string username, string password, string? ipAddress)
    {
        username = username.Trim();
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = PhTime.Now;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            await LogAttemptAsync(db, username, ipAddress, false);
            return Fail();
        }

        if (user.LockedUntil is { } until && until > now)
        {
            var minutesLeft = (int)Math.Ceiling((until - now).TotalMinutes);
            return Fail($"Too many failed attempts. Try again in {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")}.");
        }

        bool passwordOk;
        try { passwordOk = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash); }
        catch (BCrypt.Net.SaltParseException) { passwordOk = false; }

        if (!passwordOk)
        {
            if (++user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = now.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
            }
            await db.SaveChangesAsync();
            await LogAttemptAsync(db, username, ipAddress, false);
            return Fail();
        }

        // Correct password, but this site is for AMS staff only.
        if (!user.IsStaff)
        {
            await LogAttemptAsync(db, username, ipAddress, false);
            return Fail("This sign-in is for AMS staff. Office accounts use the AMS Supplies portal.");
        }
        if (!user.IsActive)
        {
            await LogAttemptAsync(db, username, ipAddress, false);
            return Fail("This account is deactivated. Ask a Super Admin to re-enable it.");
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        if (BCrypt.Net.BCrypt.PasswordNeedsRehash(user.PasswordHash, TargetBcryptCost))
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, TargetBcryptCost);

        db.UserActivities.Add(new UserActivity
        {
            UserId = user.UserId, ActivityType = "Login",
            Description = "Signed in to AMS StockWatch", IpAddress = ipAddress, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        await LogAttemptAsync(db, username, ipAddress, true);

        return new AuthResult { Succeeded = true, User = user };
    }

    private static Task LogAttemptAsync(AppDbContext db, string username, string? ip, bool ok) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO login_attempts (username, ip_address, was_successful) VALUES ({username}, {ip}, {ok})");

    private static AuthResult Fail(string? message = null) =>
        new() { Succeeded = false, ErrorMessage = message ?? GenericError };
}

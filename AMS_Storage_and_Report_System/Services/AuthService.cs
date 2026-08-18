using Microsoft.EntityFrameworkCore;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;

namespace AMS_Storage_and_Report_System.Services;

public class AuthResult
{
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public User? User { get; set; }
}

public class AuthService
{
    private readonly AppDbContext _db;

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string GenericError = "Invalid username or password.";

    public AuthService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AuthResult> ValidateLoginAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

        if (user is null || !user.IsActive)
        {
            return Fail();
        }

        if (user.LockedUntil is not null && user.LockedUntil > DateTime.UtcNow)
        {
            var minutesLeft = (int)Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return Fail($"Account locked. Try again in {minutesLeft} minute(s).");
        }

        bool passwordOk = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

        if (!passwordOk)
        {
            user.FailedLoginAttempts++;

            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
            }

            await _db.SaveChangesAsync();
            return Fail();
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return new AuthResult { Succeeded = true, User = user };
    }

    private static AuthResult Fail(string? message = null) =>
        new() { Succeeded = false, ErrorMessage = message ?? GenericError };
}
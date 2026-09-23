namespace AMS_Storage_and_Report_System.Models;

public enum UserRole
{
    SuperAdmin,
    Admin
}

public class User
{
    public uint UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePicturePath { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public bool RequirePasswordChange { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
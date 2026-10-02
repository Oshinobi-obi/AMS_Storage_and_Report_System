namespace AMS_Storage_and_Report_System.Models;

/// Must list every value of users.role. 'Office' accounts belong to the AMS Supplies
/// portal; without it here, loading any user list would throw once offices exist.
public enum UserRole
{
    SuperAdmin,
    Admin,
    Office
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
    public int? OfficeId { get; set; }   // same type as Office.OfficeId (FK must match)
    public bool IsActive { get; set; }
    public bool RequirePasswordChange { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Office? Office { get; set; }

    public bool IsStaff => Role is UserRole.SuperAdmin or UserRole.Admin;
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? FullName : DisplayName;
}

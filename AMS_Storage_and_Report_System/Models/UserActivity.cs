namespace AMS_Storage_and_Report_System.Models;

public class UserActivity
{
    public int ActivityId { get; set; }
    public uint UserId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? User { get; set; }
}

namespace AMS_Storage_and_Report_System.Models;

/// A notice AMS posts. e-SRS shows it as a banner between StartsAt and EndsAt.
public class Announcement
{
    public int AnnouncementId { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Level { get; set; } = "info";          // info | warning | urgent
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
    public uint? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    /// Live, Scheduled, Ended or Off, at the given Philippine time.
    public string StateAt(DateTime now) =>
        !IsActive ? "Off" : StartsAt > now ? "Scheduled" : EndsAt is { } e && e < now ? "Ended" : "Live";
}

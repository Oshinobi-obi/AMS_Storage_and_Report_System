namespace AMS_Storage_and_Report_System.Services;

/// One clock for the whole app. The database CURRENT_TIMESTAMP defaults and e-SRS
/// both use Philippine time, so the admin side does too.
/// Some Windows hosts don't know "Asia/Manila", so I try the Windows name next, then UTC+8.
public static class PhTime
{
    private static readonly TimeZoneInfo Manila = Find();
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Manila);
    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Manila);

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("PHT", TimeSpan.FromHours(8), "Philippine Time", "Philippine Time");
    }
}

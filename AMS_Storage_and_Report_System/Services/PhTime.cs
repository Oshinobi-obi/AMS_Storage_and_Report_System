namespace AMS_Storage_and_Report_System.Services;

/// One clock for the whole app. The database's CURRENT_TIMESTAMP defaults and the
/// AMS Supplies portal both use Philippine time, so the admin side does too.
public static class PhTime
{
    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Manila);
}

// Expiry.cs — how MFD / EXP of perishable supplies are shown (Catalog, Inventory).

using AMS_Storage_and_Report_System.Models;

namespace AMS_Storage_and_Report_System.Services;

public static class Expiry
{
    /// Items expiring within this many days are shown in amber.
    public const int SoonDays = 90;

    public static string Css(int days) => days < 0 ? "is-expired" : days <= SoonDays ? "is-soon" : "";

    public static string Icon(int days) => days < 0 ? "bi-calendar-x" : days <= SoonDays ? "bi-hourglass-split" : "bi-calendar-check";

    /// "MFD Jan 5, 2026 · EXP Mar 15, 2027 (45 days left)" or "Expired Mar 15, 2026".
    public static string Text(SupplyItem i)
    {
        if (i.ExpiresOn is not { } exp || i.DaysToExpiry is not int days) return "";
        var mfd = i.ManufacturedOn is { } m ? $"MFD {m:MMM d, yyyy} · " : "";
        return days < 0 ? $"{mfd}Expired {exp:MMM d, yyyy}"
             : days == 0 ? $"{mfd}EXP today"
             : days <= SoonDays ? $"{mfd}EXP {exp:MMM d, yyyy} ({days} day{(days == 1 ? "" : "s")} left)"
             : $"{mfd}EXP {exp:MMM d, yyyy}";
    }
}

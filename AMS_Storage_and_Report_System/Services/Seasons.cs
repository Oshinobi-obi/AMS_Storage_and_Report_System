// These are the seasonal themes AMS can turn on in e-PINS > Settings. A theme changes the colors of
// the top bar, the page headers and the sidebar of e-PINS and e-SRS, with some floating 3D
// decorations (wwwroot/css/season.css). It's saved as system_settings 'ui.season'.

using AMS_Storage_and_Report_System.Data;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public static class Seasons
{
    public const string SettingKey = "ui.season";

    /// Code, name, when "Automatic" shows it (month*100+day, inclusive), and a short description.
    public sealed record Season(string Code, string Name, int From, int To, string When, string Icon);

    public static readonly IReadOnlyList<Season> All =
    [
        new("newyear", "New Year", 101, 107, "Jan 1–7", "bi-stars"),
        new("valentines", "Valentine's Day", 207, 214, "Feb 7–14", "bi-heart-fill"),
        new("independence", "Independence Day", 605, 612, "Jun 5–12", "bi-sun-fill"),
        new("teachers", "Teachers' Month", 905, 1005, "Sep 5 – Oct 5", "bi-book-half"),
        new("halloween", "Halloween", 1020, 1102, "Oct 20 – Nov 2", "bi-moon-stars-fill"),
        new("christmas", "Christmas", 1116, 1231, "Nov 16 – Dec 31", "bi-snow"),
    ];

    public static bool IsValid(string? value) => value is "off" or "auto" || All.Any(s => s.Code == value);

    /// The season to show today for a setting. "" means none, "auto" picks by the date.
    public static string Resolve(string? setting, DateTime today) => setting switch
    {
        "auto" => ByDate(today)?.Code ?? "",
        { } code when All.Any(s => s.Code == code) => code,
        _ => "",
    };

    public static Season? ByDate(DateTime d)
    {
        var md = d.Month * 100 + d.Day;
        return All.FirstOrDefault(s => md >= s.From && md <= s.To);
    }

    public static Season? Find(string? code) => All.FirstOrDefault(s => s.Code == code);
}

/// The saved setting, read at most every 20 seconds (every page load asks for it).
public sealed class SeasonState(IDbContextFactory<AppDbContext> dbFactory, ILogger<SeasonState> log)
{
    private string? _setting;
    private DateTime _readUtc;

    public async Task<string> SettingAsync()
    {
        if (_setting is not null && DateTime.UtcNow - _readUtc < TimeSpan.FromSeconds(20)) return _setting;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var value = await db.SystemSettings.AsNoTracking()
                .Where(s => s.SettingKey == Seasons.SettingKey).Select(s => s.SettingValue).FirstOrDefaultAsync();
            _setting = Seasons.IsValid(value) ? value! : "off";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't read the seasonal theme");
            _setting ??= "off";
        }
        _readUtc = DateTime.UtcNow;
        return _setting;
    }

    /// The season to show right now ("" means none).
    public async Task<string> CurrentAsync() => Seasons.Resolve(await SettingAsync(), PhTime.Now);

    public void Remember(string setting) { _setting = setting; _readUtc = DateTime.UtcNow; }

    /// Read it again next time (the other site changed it).
    public void Forget() => _readUtc = default;
}

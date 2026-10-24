// This is for the values printed on every RIS and used in both sites,
// plus the office details (responsibility center codes, who can make requisitions).

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed class SettingsForm
{
    public string EntityName { get; set; } = "";
    public string FundCluster { get; set; } = "";
    public int ApprovedById { get; set; }
    public int IssuedById { get; set; }
    public int DefaultReorderLevel { get; set; } = 10;
}

public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory)
{
    private const string EntityKey = "ris.entity_name", FundKey = "ris.default_fund_cluster",
                         ApproverKey = "ris.approved_by_personnel_id", IssuerKey = "ris.issued_by_personnel_id",
                         ReorderKey = "stock.default_reorder";
    // This is the admin toggle "Require APP-CSE for Requisitions" (shared with e-SRS through system_settings).
    public const string RequireAppCseKey = "requisition.require_appcse";

    public async Task<SettingsForm> LoadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(x => x.SettingKey, x => x.SettingValue);
        return new SettingsForm
        {
            EntityName = s.GetValueOrDefault(EntityKey, ""),
            FundCluster = s.GetValueOrDefault(FundKey, ""),
            ApprovedById = int.TryParse(s.GetValueOrDefault(ApproverKey), out var a) ? a : 0,
            IssuedById = int.TryParse(s.GetValueOrDefault(IssuerKey), out var i) ? i : 0,
            DefaultReorderLevel = int.TryParse(s.GetValueOrDefault(ReorderKey), out var r) ? r : 10,
        };
    }

    public async Task<OpResult> SaveAsync(SettingsForm f, uint userId)
    {
        if (string.IsNullOrWhiteSpace(f.EntityName)) return OpResult.Fail("Enter the Entity Name printed on the RIS.");
        if (f.EntityName.Trim().Length > 200) return OpResult.Fail("The Entity Name is too long (200 characters at most).");
        if (f.FundCluster.Trim().Length > 10) return OpResult.Fail("The Fund Cluster can be up to 10 characters.");
        if (f.DefaultReorderLevel < 0) return OpResult.Fail("The low-stock level can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, EntityKey, f.EntityName.Trim());
        await SetAsync(db, FundKey, f.FundCluster.Trim());
        await SetAsync(db, ApproverKey, f.ApprovedById == 0 ? "" : f.ApprovedById.ToString());
        await SetAsync(db, IssuerKey, f.IssuedById == 0 ? "" : f.IssuedById.ToString());
        await SetAsync(db, ReorderKey, f.DefaultReorderLevel.ToString());
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = "Changed system settings", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success("Settings saved. New requisitions use them from now on.");
    }

    // Seasonal theme (headers and sidebar of both sites)
    public async Task<string> SeasonAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.SettingKey == Seasons.SettingKey).Select(s => s.SettingValue).FirstOrDefaultAsync();
        return Seasons.IsValid(value) ? value! : "off";
    }

    public async Task<OpResult> SaveSeasonAsync(string code, uint userId)
    {
        if (!Seasons.IsValid(code)) return OpResult.Fail("Choose a theme from the list.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, Seasons.SettingKey, code);
        var name = code switch { "off" => "off", "auto" => "Automatic", _ => Seasons.Find(code)!.Name };
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Seasonal theme set to {name}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success(code == "off" ? "Seasonal theme turned off." : $"{name} theme is on for e-PINS and e-SRS.");
    }

    // Whether offices must stay within their APP-CSE. Missing or not "0" means yes (the default).
    public async Task<bool> RequireAppCseAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var v = await db.SystemSettings.AsNoTracking()
            .Where(x => x.SettingKey == RequireAppCseKey).Select(x => x.SettingValue).FirstOrDefaultAsync();
        return v != "0";
    }

    public async Task<OpResult> SaveRequireAppCseAsync(bool require, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, RequireAppCseKey, require ? "1" : "0");
        db.UserActivities.Add(new UserActivity
        {
            UserId = userId, ActivityType = "Update",
            Description = require ? "Turned on APP-CSE requirement for requisitions" : "Turned off APP-CSE requirement for requisitions",
            CreatedAt = PhTime.Now,
        });
        await db.SaveChangesAsync();
        return OpResult.Success(require
            ? "APP-CSE is now required. Offices can only request items in their APP-CSE, within their balance."
            : "APP-CSE requirement is off. Offices can request any catalog item in any quantity.");
    }

    private static async Task SetAsync(AppDbContext db, string key, string value)
    {
        var row = await db.SystemSettings.FindAsync(key);
        if (row is null) db.SystemSettings.Add(new SystemSetting { SettingKey = key, SettingValue = value });
        else row.SettingValue = value;
    }

    public async Task<List<Office>> OfficesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Offices.AsNoTracking().OrderBy(o => o.OfficeAcronym).ToListAsync();
    }

    public async Task<OpResult> SaveOfficeAsync(int officeId, string? rcCode, bool canRequisition, string? appCseSheet, string? risCode, int appCseOfficeId, uint userId)
    {
        risCode = string.IsNullOrWhiteSpace(risCode) ? null : risCode.Trim();
        if (risCode is not null && (risCode.Length > 5 || !risCode.All(char.IsDigit)))
            return OpResult.Fail("The RIS code is a number of up to 5 digits, like 01 or 010.");
        rcCode = string.IsNullOrWhiteSpace(rcCode) ? null : rcCode.Trim();
        appCseSheet = string.IsNullOrWhiteSpace(appCseSheet) ? null : appCseSheet.Trim();
        if (rcCode?.Length > 30) return OpResult.Fail("The responsibility center code can be up to 30 characters.");
        if (appCseSheet?.Length > 50) return OpResult.Fail("The APP-CSE sheet name can be up to 50 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var o = await db.Offices.FindAsync(officeId);
        if (o is null) return OpResult.Fail("This office no longer exists.");
        o.ResponsibilityCenterCode = rcCode;
        o.RisCode = risCode;
        if (appCseOfficeId == officeId) return OpResult.Fail("An office can't use its own APP-CSE as another office's. Choose \"Its own\".");
        if (appCseOfficeId != 0)
        {
            var target = await db.Offices.FindAsync(appCseOfficeId);
            if (target is null) return OpResult.Fail("That office no longer exists.");
            if (target.AppCseOfficeId is not null) return OpResult.Fail($"{target.OfficeAcronym} itself uses another office's APP-CSE. Choose the division.");
            if (await db.Offices.AnyAsync(x => x.AppCseOfficeId == officeId))
                return OpResult.Fail($"Other offices use {o.OfficeAcronym}'s APP-CSE, so it must keep its own.");
        }
        o.AppCseOfficeId = appCseOfficeId == 0 ? null : appCseOfficeId;
        o.CanRequisition = canRequisition;
        o.AppCseSheet = appCseSheet is not null && appCseSheet.Equals(o.OfficeAcronym, StringComparison.OrdinalIgnoreCase) ? null : appCseSheet;
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Updated office {o.OfficeAcronym}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success($"{o.OfficeAcronym} saved.");
    }
}

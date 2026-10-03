// SettingsService.cs — values printed on every RIS and used across both sites,
// plus office details (responsibility center codes, who may requisition).

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

    public async Task<OpResult> SaveOfficeAsync(int officeId, string? rcCode, bool canRequisition, uint userId)
    {
        rcCode = string.IsNullOrWhiteSpace(rcCode) ? null : rcCode.Trim();
        if (rcCode?.Length > 30) return OpResult.Fail("The responsibility center code can be up to 30 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var o = await db.Offices.FindAsync(officeId);
        if (o is null) return OpResult.Fail("This office no longer exists.");
        o.ResponsibilityCenterCode = rcCode;
        o.CanRequisition = canRequisition;
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Updated office {o.OfficeAcronym}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success($"{o.OfficeAcronym} saved.");
    }
}

// This decides whose APP-CSE an office uses.
// Most offices use their own. A unit set to share its division's (offices.appcse_office_id,
// like LRPO under ESSD) is approved against the division's APP-CSE, together with the
// division's own requisitions.

using AMS_Storage_and_Report_System.Data;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public readonly record struct AppCseScope(int BudgetOfficeId, IReadOnlyList<int> SharingOfficeIds)
{
    public static async Task<AppCseScope> ForAsync(AppDbContext db, int officeId)
    {
        var budget = await db.Offices.Where(o => o.OfficeId == officeId)
            .Select(o => o.AppCseOfficeId ?? o.OfficeId).FirstOrDefaultAsync();
        if (budget == 0) budget = officeId;
        var sharing = await db.Offices.Where(o => (o.AppCseOfficeId ?? o.OfficeId) == budget)
            .Select(o => o.OfficeId).ToListAsync();
        return new AppCseScope(budget, sharing.Count > 0 ? sharing : [officeId]);
    }

    /// Every office and the office whose APP-CSE it uses.
    public static Task<Dictionary<int, int>> BudgetMapAsync(AppDbContext db) =>
        db.Offices.AsNoTracking().ToDictionaryAsync(o => o.OfficeId, o => o.AppCseOfficeId ?? o.OfficeId);
}

// This is the property register (property_items) and the numbers
// behind the property charts on the dashboard.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

/// One bar or slice: a label, its value, and optionally a color and a link.
public sealed record ChartPoint(string Label, decimal Value, string? Color = null, string? Href = null, string? Note = null, string? Tooltip = null);

public sealed class PropertyDashboard
{
    public int TotalPpe { get; set; }
    public int TotalSemiExpendable { get; set; }
    public int TotalUnits => TotalPpe + TotalSemiExpendable;
    public decimal AcquisitionCost { get; set; }
    public List<ChartPoint> ByStatus { get; set; } = [];
    public List<ChartPoint> ByClassification { get; set; } = [];
    public List<ChartPoint> ByOffice { get; set; } = [];
    public List<ChartPoint> Accountability { get; set; } = [];
    public List<ChartPoint> DocumentsByType { get; set; } = [];
    public List<ChartPoint> DocumentsByPropertyType { get; set; } = [];
    public List<ChartPoint> Requisitions { get; set; } = [];
}

public sealed class PropertyService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Register
    public async Task<List<PropertyItem>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PropertyItems.AsNoTracking()
            .Include(p => p.Office).Include(p => p.Personnel).Include(p => p.PropertyType)
            .OrderBy(p => p.PropertyNo).ToListAsync();
    }

    public async Task<OpResult> SaveAsync(PropertyItem f, uint userId)
    {
        var no = f.PropertyNo.Trim();
        var desc = f.Description.Trim();
        if (no.Length is 0 or > 50) return OpResult.Fail("Enter the property number (up to 50 characters).");
        if (desc.Length == 0) return OpResult.Fail("Enter a description of the property.");
        if (!PropertyStatus.Classifications.Contains(f.Classification)) return OpResult.Fail("Choose a classification.");
        if (f.PropertyTypeId is not (PropertyStatus.SemiExpendableTypeId or PropertyStatus.PpeTypeId)) return OpResult.Fail("Choose PPE or Semi-Expendable.");
        if (f.Quantity < 1) return OpResult.Fail("The quantity must be at least 1.");
        if (f.UnitCost < 0) return OpResult.Fail("The unit cost can't be negative.");
        if (!PropertyStatus.IsValid(f.Status)) return OpResult.Fail("Choose a status.");
        if (f.AcquiredOn?.Date > PhTime.Now.Date) return OpResult.Fail("The acquisition date can't be in the future.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.PropertyItems.AnyAsync(p => p.PropertyNo == no && p.PropertyId != f.PropertyId))
            return OpResult.Fail($"Property no. {no} is already in the register.");
        if (f.PersonnelId is int pid && f.OfficeId is int oid &&
            !await db.RoPersonnel.AnyAsync(p => p.PersonnelId == pid && p.OfficeId == oid))
            return OpResult.Fail("The accountable person must belong to the selected office.");

        var item = f.PropertyId == 0 ? new PropertyItem { CreatedBy = userId } : await db.PropertyItems.FindAsync(f.PropertyId);
        if (item is null) return OpResult.Fail("This property is no longer in the register.");
        var oldStatus = item.Status;

        item.PropertyNo = no;
        item.Description = desc[..Math.Min(desc.Length, 500)];
        item.Classification = f.Classification;
        item.EquipmentType = string.IsNullOrWhiteSpace(f.EquipmentType) ? null : f.EquipmentType.Trim()[..Math.Min(f.EquipmentType.Trim().Length, 80)];
        item.PropertyTypeId = f.PropertyTypeId;
        item.Quantity = f.Quantity;
        item.UnitCost = Math.Round(f.UnitCost, 2);
        item.AcquiredOn = f.AcquiredOn?.Date;
        item.OfficeId = f.OfficeId;
        item.PersonnelId = f.OfficeId is null ? null : f.PersonnelId;
        item.Status = f.Status;
        if (oldStatus != f.Status) item.StatusBeforeReturn = null;   // changed by hand, so nothing to put back
        item.Remarks = string.IsNullOrWhiteSpace(f.Remarks) ? null : f.Remarks.Trim()[..Math.Min(f.Remarks.Trim().Length, 500)];
        if (f.PropertyId == 0) db.PropertyItems.Add(item);

        var what = f.PropertyId == 0 ? $"Added property {no}"
                 : oldStatus != f.Status ? $"Property {no}: {oldStatus} → {f.Status}"
                 : $"Edited property {no}";
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = f.PropertyId == 0 ? "Create" : "Update", Description = what, CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success(f.PropertyId == 0 ? $"{no} was added to the register." : "Changes saved.");
    }

    public async Task<OpResult> DeleteAsync(int propertyId, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.PropertyItems.FindAsync(propertyId);
        if (item is null) return OpResult.Fail("This property is no longer in the register.");
        db.PropertyItems.Remove(item);
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Delete", Description = $"Removed property {item.PropertyNo} from the register", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success($"{item.PropertyNo} was removed from the register.");
    }

    // Dashboard numbers
    // `since` limits the document and requisition charts to a period (null means all time).
    public async Task<PropertyDashboard> DashboardAsync(DateTime? since = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = new PropertyDashboard();

        // I count units (quantity), so a lot of 10 chairs counts as 10.
        var rows = await db.PropertyItems.AsNoTracking()
            .Select(p => new { p.Status, p.Classification, p.PropertyTypeId, p.Quantity, p.UnitCost, p.PersonnelId, Office = p.Office != null ? p.Office.OfficeAcronym : null })
            .ToListAsync();

        d.TotalPpe = rows.Where(r => r.PropertyTypeId == PropertyStatus.PpeTypeId).Sum(r => r.Quantity);
        d.TotalSemiExpendable = rows.Where(r => r.PropertyTypeId == PropertyStatus.SemiExpendableTypeId).Sum(r => r.Quantity);
        d.AcquisitionCost = rows.Sum(r => r.Quantity * r.UnitCost);

        d.ByStatus = PropertyStatus.All
            .Select(s => new ChartPoint(s.Name, rows.Where(r => r.Status == s.Name).Sum(r => r.Quantity), s.Color,
                                        $"/property-register?status={Uri.EscapeDataString(s.Name)}"))
            .Where(p => p.Value > 0).ToList();

        d.ByClassification = rows.GroupBy(r => r.Classification)
            .Select(g => new ChartPoint(g.Key, g.Sum(r => r.Quantity), null, $"/property-register?class={Uri.EscapeDataString(g.Key)}"))
            .OrderByDescending(p => p.Value).ToList();

        d.ByOffice = rows.GroupBy(r => r.Office ?? "No office")
            .Select(g => new ChartPoint(g.Key, g.Sum(r => r.Quantity)))
            .OrderByDescending(p => p.Value).Take(10).ToList();

        d.Accountability =
        [
            new("Assigned to a person", rows.Where(r => r.PersonnelId != null).Sum(r => r.Quantity), "#1e9e5a"),
            new("Unassigned", rows.Where(r => r.PersonnelId == null).Sum(r => r.Quantity), "#e0a400"),
            new("Transferred", rows.Where(r => r.Status == "Transferred").Sum(r => r.Quantity), "#6f7a90"),
            new("Returned", rows.Where(r => r.Status == "Returned").Sum(r => r.Quantity), "#3aa1a6"),
        ];

        // Property documents (PAR / ICS / return slips) for the pie charts
        var docs = await db.PropertyDocuments.AsNoTracking()
            .Where(x => since == null || x.UploadedAt >= since)
            .Select(x => new { x.DocumentType, Type = x.PropertyType != null ? x.PropertyType.TypeName : "" }).ToListAsync();
        d.DocumentsByType =
        [
            new("PAR", docs.Count(x => x.DocumentType == "PAR"), "#0b2fa8", "/property-documents"),
            new("ICS", docs.Count(x => x.DocumentType == "ICS"), "#fdb515", "/property-documents"),
            new("Return slip", docs.Count(x => x.DocumentType == "Return Slip"), "#f07a2a", "/property-documents"),
        ];
        d.DocumentsByPropertyType =
        [
            new("PPE", docs.Count(x => x.Type.Contains("PPE")), "#0b2fa8", "/property-documents"),
            new("Semi-expendable", docs.Count(x => x.Type.Contains("Semi")), "#1e9e5a", "/property-documents"),
        ];

        // Requisition (RIS) monitoring
        var ris = await db.RisTransactions.AsNoTracking()
            .Where(r => since == null || r.RequestedAt >= since)
            .GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        d.Requisitions =
        [
            new("Pending approval", ris.GetValueOrDefault(RisStatus.Pending), "#e0a400", "/requisitions"),
            new("For issuance", ris.GetValueOrDefault(RisStatus.Approved), "#2f6fde", "/requisitions"),
            new("Issued", ris.GetValueOrDefault(RisStatus.Issued), "#1e9e5a", "/requisitions"),
            new("Rejected", ris.GetValueOrDefault(RisStatus.Rejected), "#d63a3a", "/requisitions"),
            new("Cancelled", ris.GetValueOrDefault(RisStatus.Cancelled), "#6f7a90", "/requisitions"),
        ];
        return d;
    }
}

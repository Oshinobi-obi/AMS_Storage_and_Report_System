// This handles which register items are on a PAR / ICS, and what happens to them
// when the document is returned.
//
//   Linking an item to a PAR / ICS makes the document's person (and office) accountable for it.
//   When the PAR / ICS is returned (its Return Slip is uploaded), its items change to "Returned",
//   and I keep the status they had in status_before_return.
//   "Mark as not returned", or deleting the Return Slip, puts that status back.
//
// The My accountability page of e-SRS only counts linked items that aren't "Returned".

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

/// A register item that can be put on a PAR / ICS.
public sealed record LinkCandidate(
    int PropertyId, string PropertyNo, string Description, int Quantity, decimal UnitCost, string Status,
    int? PersonnelId, string? HolderName, int? DocumentId, bool IsMine)
{
    public decimal Total => Quantity * UnitCost;
}

/// How much property is on one document.
public sealed record LinkedSummary(int Items, int Units, decimal Total);

public sealed class PropertyDocumentLinks(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string ReturnedStatus = "Returned";

    /// Register items of the document's type of property that can go on it, meaning the ones not
    /// on another PAR / ICS that someone still has. The person's own items come first.
    public async Task<List<LinkCandidate>> CandidatesAsync(int propertyTypeId, int personnelId, int? documentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var activeDocs = db.PropertyDocuments.Where(d => d.ReturnedOn == null).Select(d => d.DocumentId);
        var rows = await db.PropertyItems.AsNoTracking()
            .Where(i => i.PropertyTypeId == propertyTypeId)
            .Where(i => i.DocumentId == null || i.DocumentId == documentId || !activeDocs.Contains(i.DocumentId.Value))
            .Select(i => new LinkCandidate(i.PropertyId, i.PropertyNo, i.Description, i.Quantity, i.UnitCost, i.Status,
                i.PersonnelId, i.Personnel != null ? i.Personnel.FullName : null,
                i.DocumentId, i.PersonnelId == personnelId))
            .ToListAsync();
        return rows
            .OrderByDescending(r => documentId != null && r.DocumentId == documentId)
            .ThenByDescending(r => r.IsMine)
            .ThenBy(r => r.PersonnelId != null)
            .ThenBy(r => r.PropertyNo)
            .ToList();
    }

    /// Item ids that are on a document right now.
    public async Task<HashSet<int>> LinkedIdsAsync(int documentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.PropertyItems.Where(i => i.DocumentId == documentId).Select(i => i.PropertyId).ToListAsync()).ToHashSet();
    }

    /// Items, units and value on each of the given documents.
    public async Task<Dictionary<int, LinkedSummary>> SummariesAsync(IReadOnlyCollection<int> documentIds)
    {
        if (documentIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.PropertyItems.AsNoTracking()
            .Where(i => i.DocumentId != null && documentIds.Contains(i.DocumentId.Value))
            .GroupBy(i => i.DocumentId!.Value)
            .Select(g => new { g.Key, Items = g.Count(), Units = g.Sum(i => i.Quantity), Total = g.Sum(i => i.Quantity * i.UnitCost) })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => new LinkedSummary(r.Items, r.Units, r.Total));
    }

    /// Puts exactly these items on the document (call SaveChanges after). Items taken off go back
    /// to the status they had if the return of the document changed it.
    public async Task<(int Added, int Removed)> SetLinksAsync(AppDbContext db, PropertyDocument doc, IReadOnlyCollection<int> propertyIds)
    {
        var wanted = propertyIds.ToHashSet();
        var current = await db.PropertyItems.Where(i => i.DocumentId == doc.DocumentId).ToListAsync();
        var removed = 0;
        foreach (var item in current.Where(i => !wanted.Contains(i.PropertyId)))
        {
            item.DocumentId = null;
            RestoreStatus(item);
            removed++;
        }

        var currentIds = current.Select(i => i.PropertyId).ToHashSet();
        var toAdd = wanted.Where(id => !currentIds.Contains(id)).ToList();
        var adding = toAdd.Count == 0 ? [] : await db.PropertyItems.Where(i => toAdd.Contains(i.PropertyId)).ToListAsync();
        var onlyEquipment = PropertyStatus.SplitEquipment(doc.EquipmentType) is { Count: 1 } eq ? eq[0] : null;
        foreach (var item in adding)
        {
            item.DocumentId = doc.DocumentId;
            if (string.IsNullOrWhiteSpace(item.EquipmentType) && onlyEquipment is not null) item.EquipmentType = onlyEquipment[..Math.Min(onlyEquipment.Length, 80)];
            item.PersonnelId = doc.PersonnelId;
            item.OfficeId = doc.OfficeId;
            if (doc.ReturnedOn is not null)
            {
                // Linked to a PAR / ICS that was already returned, so the property is back with AMS.
                if (item.Status != ReturnedStatus) { item.StatusBeforeReturn = item.Status; item.Status = ReturnedStatus; }
            }
            else if (item.Status is ReturnedStatus or "Newly Acquired")
            {
                // Issued to the person on this document
                item.Status = item.StatusBeforeReturn is { } before && before != ReturnedStatus ? before : PropertyStatus.Serviceable;
                item.StatusBeforeReturn = null;
            }
        }
        return (adding.Count, removed);
    }

    /// A document with one kind of equipment (like Laptop) gives it to its register items that have none yet
    /// (call SaveChanges after).
    public async Task<int> FillEquipmentAsync(AppDbContext db, PropertyDocument doc)
    {
        if (PropertyStatus.SplitEquipment(doc.EquipmentType) is not { Count: 1 } eq) return 0;
        var name = eq[0][..Math.Min(eq[0].Length, 80)];
        var items = await db.PropertyItems.Where(i => i.DocumentId == doc.DocumentId && (i.EquipmentType == null || i.EquipmentType == "")).ToListAsync();
        foreach (var i in items) i.EquipmentType = name;
        return items.Count;
    }

    /// The PAR / ICS was returned, so its items become "Returned" (call SaveChanges after).
    public async Task<int> MarkReturnedAsync(AppDbContext db, int documentId)
    {
        var items = await db.PropertyItems.Where(i => i.DocumentId == documentId && i.Status != ReturnedStatus).ToListAsync();
        foreach (var item in items) { item.StatusBeforeReturn = item.Status; item.Status = ReturnedStatus; }
        return items.Count;
    }

    /// The return was undone, so the items go back to the status they had (call SaveChanges after).
    public async Task<int> UndoReturnAsync(AppDbContext db, IReadOnlyCollection<int> documentIds)
    {
        if (documentIds.Count == 0) return 0;
        var items = await db.PropertyItems
            .Where(i => i.DocumentId != null && documentIds.Contains(i.DocumentId.Value) && i.Status == ReturnedStatus)
            .ToListAsync();
        foreach (var item in items) RestoreStatus(item);
        return items.Count;
    }

    private static void RestoreStatus(PropertyItem item)
    {
        if (item.Status != ReturnedStatus || item.StatusBeforeReturn is null) return;
        item.Status = item.StatusBeforeReturn;
        item.StatusBeforeReturn = null;
    }
}

// This is for the deliveries received in Supplies inventory and their scanned documents:
// the Delivery Receipt (DR), the Sales Invoice and the Inspection and Acceptance Report (IAR),
// scanned together into one PDF. They're listed in Reports > Deliveries.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

/// Where the delivery PDFs are saved: Storage:DeliveryDocumentsPath, or a "delivery-documents"
/// folder next to the property documents folder.
public sealed class DeliveryDocumentStorage
{
    public string RootPath { get; }

    public DeliveryDocumentStorage(IConfiguration config, IWebHostEnvironment env, PropertyDocumentStorage propertyDocs)
    {
        var configured = config["Storage:DeliveryDocumentsPath"];
        RootPath = !string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, configured)
            : Path.Combine(Path.GetDirectoryName(propertyDocs.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? env.ContentRootPath, "delivery-documents");
        Directory.CreateDirectory(RootPath);
    }

    public const long MaxBytes = 50 * 1024 * 1024;

    /// The saved file of a delivery, or null if it has none or the file is missing.
    public string? PathOf(SupplyDelivery d)
    {
        if (string.IsNullOrWhiteSpace(d.FilePath)) return null;
        var full = Path.Combine(RootPath, Path.GetFileName(d.FilePath));
        return File.Exists(full) ? full : null;
    }

    /// Saves an uploaded PDF with a new name and returns that name.
    public async Task<string> SaveAsync(Stream source, CancellationToken ct = default)
    {
        var name = $"{Guid.NewGuid()}.pdf";
        await using var target = new FileStream(Path.Combine(RootPath, name), FileMode.CreateNew);
        await source.CopyToAsync(target, ct);
        return name;
    }

    public void Delete(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)) return;
        var full = Path.Combine(RootPath, Path.GetFileName(storedName));
        if (File.Exists(full)) File.Delete(full);
    }
}

public sealed record DeliveryRow(int DeliveryId, string ReferenceNo, DateTime ReceivedOn, string? Supplier, string? Remarks,
                                 bool HasFile, string? FileName, int Items, int Units, decimal Amount, string? RecordedBy);

public sealed record DeliveryLine(string StockNo, string Item, string Unit, int Quantity, decimal? UnitCost)
{
    public decimal Amount => Quantity * (UnitCost ?? 0);
}

public sealed class DeliveryService(IDbContextFactory<AppDbContext> dbFactory, DeliveryDocumentStorage storage)
{
    /// Deliveries received from `from` up to `to` (not including it), newest first.
    public async Task<List<DeliveryRow>> ListAsync(DateTime from, DateTime to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var deliveries = await db.SupplyDeliveries.AsNoTracking()
            .Where(d => d.ReceivedOn >= from && d.ReceivedOn < to)
            .OrderByDescending(d => d.ReceivedOn).ThenByDescending(d => d.DeliveryId).ToListAsync();
        var ids = deliveries.Select(d => d.DeliveryId).ToList();
        var totals = await db.StockMovements.AsNoTracking()
            .Where(m => m.DeliveryId != null && ids.Contains(m.DeliveryId.Value))
            .GroupBy(m => m.DeliveryId!.Value)
            .Select(g => new { g.Key, Items = g.Count(), Units = g.Sum(m => m.Quantity), Amount = g.Sum(m => m.Quantity * (m.UnitCost ?? 0)) })
            .ToDictionaryAsync(x => x.Key);
        var userIds = deliveries.Select(d => d.RecordedBy).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName);
        return deliveries.Select(d =>
        {
            totals.TryGetValue(d.DeliveryId, out var t);
            return new DeliveryRow(d.DeliveryId, d.ReferenceNo, d.ReceivedOn, d.Supplier, d.Remarks,
                storage.PathOf(d) is not null, d.OriginalFileName, t?.Items ?? 0, t?.Units ?? 0, t?.Amount ?? 0,
                users.GetValueOrDefault(d.RecordedBy));
        }).ToList();
    }

    public async Task<List<DeliveryLine>> LinesAsync(int deliveryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.StockMovements.AsNoTracking()
            .Where(m => m.DeliveryId == deliveryId)
            .Join(db.SupplyItems, m => m.ItemId, i => i.ItemId, (m, i) => new { i.StockNo, i.ItemName, i.UnitOfMeasure, m.Quantity, m.UnitCost })
            .OrderBy(x => x.ItemName).ToListAsync();
        return rows.Select(x => new DeliveryLine(x.StockNo, x.ItemName, x.UnitOfMeasure, x.Quantity, x.UnitCost)).ToList();
    }

    public async Task<SupplyDelivery?> FindAsync(int deliveryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SupplyDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.DeliveryId == deliveryId);
    }

    /// Attaches (or replaces) the scanned PDF of a delivery. The old file gets removed.
    public async Task<OpResult> AttachAsync(int deliveryId, string storedName, string originalName, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await db.SupplyDeliveries.FindAsync(deliveryId);
        if (d is null) { storage.Delete(storedName); return OpResult.Fail("This delivery is no longer on record."); }
        var old = d.FilePath;
        d.FilePath = storedName;
        d.OriginalFileName = Path.GetFileName(originalName);
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Attached the scanned documents of delivery {d.ReferenceNo}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        if (old != storedName) storage.Delete(old);
        return OpResult.Success($"Scanned documents of {d.ReferenceNo} saved.");
    }
}

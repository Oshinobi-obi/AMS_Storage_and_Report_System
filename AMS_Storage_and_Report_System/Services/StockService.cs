// StockService.cs — everything that changes the warehouse count outside of RIS:
// deliveries (receipts) and corrections (adjustments). Each change is written to the
// stock ledger (stock_movements), which is also the Stock Card for that item.

using System.Data;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed class ReceiptLine
{
    public int ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
}

public sealed record StockCardRow(DateTime Date, string Type, string? Reference, string? Office,
                                  int Receipt, int Issue, int Balance, decimal? UnitCost, string? Remarks);

public sealed class StockService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// Records a delivery (e.g. from PS-DBM) with its IAR/DR number.
    public async Task<OpResult> ReceiveAsync(uint userId, string reference, DateTime receivedOn, string? remarks,
                                             IReadOnlyList<ReceiptLine> lines, bool updatePrices)
    {
        reference = reference.Trim();
        if (reference.Length is 0 or > 50) return OpResult.Fail("Enter the IAR or delivery receipt number (up to 50 characters).");
        if (receivedOn.Date > PhTime.Now.Date) return OpResult.Fail("The delivery date can't be in the future.");
        var valid = lines.Where(l => l.ItemId != 0).ToList();
        if (valid.Count == 0) return OpResult.Fail("Add at least one item.");
        if (valid.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) return OpResult.Fail("Each item can only be listed once. Combine the quantities.");
        if (valid.Any(l => l.Quantity <= 0)) return OpResult.Fail("Every quantity must be at least 1.");
        if (valid.Any(l => l.UnitCost < 0)) return OpResult.Fail("Unit costs can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var at = receivedOn.Date == PhTime.Now.Date ? PhTime.Now : receivedOn.Date.AddHours(12);

        foreach (var l in valid.OrderBy(l => l.ItemId))
        {
            var rows = updatePrices && l.UnitCost > 0
                ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supply_items SET stock_on_hand = stock_on_hand + {l.Quantity}, unit_price = {l.UnitCost} WHERE item_id = {l.ItemId}")
                : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supply_items SET stock_on_hand = stock_on_hand + {l.Quantity} WHERE item_id = {l.ItemId}");
            if (rows == 0) return OpResult.Fail("One of the items no longer exists. Reload the page.");

            db.StockMovements.Add(new StockMovement
            {
                ItemId = l.ItemId, MovementType = "RECEIPT", Quantity = l.Quantity,
                BalanceAfter = await BalanceAsync(db, l.ItemId),
                UnitCost = l.UnitCost > 0 ? l.UnitCost : null,
                ReferenceType = "IAR", ReferenceNo = reference,
                Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim(),
                PerformedBy = userId, CreatedAt = at,
            });
        }
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Received delivery {reference} ({valid.Count} items)", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success($"Delivery {reference} recorded. {valid.Count} item(s) added to the warehouse count.");
    }

    /// Corrects the count after a physical inventory, damage, expiry, etc.
    public async Task<OpResult> AdjustAsync(uint userId, int itemId, bool increase, int quantity, string reason)
    {
        reason = reason.Trim();
        if (quantity <= 0) return OpResult.Fail("Enter a quantity of at least 1.");
        if (reason.Length < 5) return OpResult.Fail("Write the reason for the adjustment, for example \"Physical count, Oct 2026\" or \"Damaged by water\".");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var rows = increase
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supply_items SET stock_on_hand = stock_on_hand + {quantity} WHERE item_id = {itemId}")
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE supply_items SET stock_on_hand = stock_on_hand - {quantity} WHERE item_id = {itemId} AND stock_on_hand >= {quantity}");
        if (rows == 0)
            return OpResult.Fail(increase ? "This item no longer exists." : "You can't remove more than what's in the warehouse.");

        db.StockMovements.Add(new StockMovement
        {
            ItemId = itemId, MovementType = increase ? "ADJUSTMENT_IN" : "ADJUSTMENT_OUT", Quantity = quantity,
            BalanceAfter = await BalanceAsync(db, itemId), ReferenceType = "ADJ",
            ReferenceNo = $"ADJ-{PhTime.Now:yyyyMMdd-HHmm}", Remarks = reason[..Math.Min(reason.Length, 500)],
            PerformedBy = userId, CreatedAt = PhTime.Now,
        });
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Stock adjustment {(increase ? "+" : "-")}{quantity} on item #{itemId}: {reason}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success("Stock adjusted.");
    }

    public async Task<(SupplyItem? Item, List<StockCardRow> Rows)> StockCardAsync(int itemId, DateTime? from = null, DateTime? to = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.SupplyItems.AsNoTracking().Include(i => i.Category).FirstOrDefaultAsync(i => i.ItemId == itemId);
        if (item is null) return (null, []);

        var q = db.StockMovements.AsNoTracking().Where(m => m.ItemId == itemId);
        if (from is { } f) q = q.Where(m => m.CreatedAt >= f.Date);
        if (to is { } t) q = q.Where(m => m.CreatedAt < t.Date.AddDays(1));

        var rows = await q.OrderBy(m => m.CreatedAt).ThenBy(m => m.MovementId)
            .Select(m => new
            {
                m.CreatedAt, m.MovementType, m.ReferenceNo, m.Quantity, m.BalanceAfter, m.UnitCost, m.Remarks,
                Office = db.Offices.Where(o => o.OfficeId == m.OfficeId).Select(o => o.OfficeAcronym).FirstOrDefault(),
            }).ToListAsync();

        return (item, rows.Select(r =>
        {
            var inbound = r.MovementType is "OPENING" or "RECEIPT" or "RETURN" or "ADJUSTMENT_IN";
            return new StockCardRow(r.CreatedAt, MovementLabel(r.MovementType), r.ReferenceNo, r.Office,
                inbound ? r.Quantity : 0, inbound ? 0 : r.Quantity, r.BalanceAfter, r.UnitCost, r.Remarks);
        }).ToList());
    }

    public static string MovementLabel(string type) => type switch
    {
        "OPENING" => "Opening balance",
        "RECEIPT" => "Delivery",
        "ISSUE" => "Issued (RIS)",
        "RETURN" => "Returned",
        "ADJUSTMENT_IN" => "Adjustment (+)",
        "ADJUSTMENT_OUT" => "Adjustment (−)",
        _ => type,
    };

    private static async Task<int> BalanceAsync(AppDbContext db, int itemId) =>
        (int)await db.Database
            .SqlQuery<long>($"SELECT CAST(stock_on_hand AS SIGNED) AS Value FROM supply_items WHERE item_id = {itemId}")
            .SingleAsync();
}

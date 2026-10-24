// This is the AMS side of the RIS process:
//   PendingApproval, then (approve) ApprovedForIssuance, then (release) Issued
//                    or (reject) Rejected
// Approving uses up the office's APP-CSE and reserves the warehouse stock (nothing leaves
// the stockroom yet). The stock on hand only goes down when AMS marks the RIS as issued,
// meaning when the items are actually handed over. Row locks stop two approvals from
// promising the same items, and they match the locks in e-SRS.

using System.Data;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed record OpResult(bool Ok, string Message, IReadOnlyList<string>? Details = null)
{
    /// Id of the record that was just made (when it matters).
    public int? Id { get; init; }

    public static OpResult Success(string message) => new(true, message);
    public static OpResult Fail(string message, IReadOnlyList<string>? details = null) => new(false, message, details);
    public string FullMessage => Details is { Count: > 0 } d ? $"{Message} {string.Join(" ", d)}" : Message;
}

public sealed class QueueRow
{
    public int RisId { get; set; }
    public string RisNo { get; set; } = "";
    public string OfficeAcronym { get; set; } = "";
    public string OfficeName { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public int ItemCount { get; set; }
    public decimal Amount { get; set; }
    public int OfficeId { get; set; }
}

/// What AMS needs to see next to each line before approving.
public sealed record LineContext(int StockOnHand, int ReorderLevel, int? Allocated, int? AlreadyIssued, int OtherPending, int Reserved = 0)
{
    /// On hand minus what other approved requisitions are still waiting to pick up.
    public int Available => Math.Max(StockOnHand - Reserved, 0);

    /// APP-CSE left for this line after counting this request (null means the item isn't in the APP-CSE).
    public int? RemainingIncludingThis(int requested) =>
        Allocated is null ? null : Allocated.Value - (AlreadyIssued ?? 0) - OtherPending - requested;
}

public sealed record LineDecision(int IssueQty, string? Remarks);

public sealed class RequisitionAdminService(IDbContextFactory<AppDbContext> dbFactory, ILogger<RequisitionAdminService> log)
{
    // This reads the admin toggle "Require APP-CSE for Requisitions" (system_settings).
    // Missing or anything other than "0" means on, so existing databases keep the strict rule.
    public const string RequireAppCseKey = "requisition.require_appcse";
    private static async Task<bool> RequireAppCseAsync(AppDbContext db)
    {
        var v = await db.SystemSettings.AsNoTracking()
            .Where(x => x.SettingKey == RequireAppCseKey).Select(x => x.SettingValue).FirstOrDefaultAsync();
        return v != "0";
    }

    // Lets the review page show the APP-CSE column neutrally when the requirement is off.
    public async Task<bool> AppCseRequiredAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await RequireAppCseAsync(db);
    }

    // Queries
    public async Task<int> CountPendingAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RisTransactions.CountAsync(r => r.Status == RisStatus.Pending);
    }

    public async Task<Dictionary<string, int>> CountByStatusAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RisTransactions.GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<List<QueueRow>> QueueAsync(string? status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.RisTransactions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);

        var rows = await q.Select(r => new QueueRow
        {
            RisId = r.RisId,
            RisNo = r.RisNo,
            OfficeId = r.OfficeId,
            OfficeAcronym = r.Office!.OfficeAcronym,
            OfficeName = r.OfficeName,
            Purpose = r.Purpose,
            Status = r.Status,
            RequestedAt = r.RequestedAt,
            ItemCount = r.Items.Count,
            Amount = r.Items.Sum(i => i.UnitCost * (i.IssuedQty ?? i.RequestedQty)),
        }).ToListAsync();

        // Oldest pending first (first come, first served), everything else newest first.
        return status == RisStatus.Pending
            ? rows.OrderBy(r => r.RequestedAt).ToList()
            : rows.OrderByDescending(r => r.RequestedAt).ToList();
    }

    public async Task<RisTransaction?> GetAsync(int risId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RisTransactions.AsNoTracking()
            .Include(r => r.Office)
            .Include(r => r.Items.OrderBy(i => i.LineNo)).ThenInclude(i => i.Item)
            .Include(r => r.History.OrderBy(h => h.ChangedAt)).ThenInclude(h => h.ChangedByUser)
            .Include(r => r.RequestedByPersonnel)
            .Include(r => r.ApprovedByPersonnel)
            .Include(r => r.IssuedByPersonnel)
            .Include(r => r.ReceivedByPersonnel)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.RisId == risId);
    }

    /// Current stock and APP-CSE numbers for every line of an RIS (by ItemId).
    public async Task<Dictionary<int, LineContext>> LineContextAsync(RisTransaction ris)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var itemIds = ris.Items.Select(i => i.ItemId).ToList();

        var stock = await db.SupplyItems.AsNoTracking().Where(i => itemIds.Contains(i.ItemId))
            .ToDictionaryAsync(i => i.ItemId, i => (i.StockOnHand, i.ReorderLevel));
        // An office can request on its division's APP-CSE (like LRPO on ESSD's), see AppCseScope.
        var scope = await AppCseScope.ForAsync(db, ris.OfficeId);
        var sharing = scope.SharingOfficeIds.ToList();
        var alloc = await db.AppCseAllocations.AsNoTracking()
            .Where(a => a.OfficeId == scope.BudgetOfficeId && a.FiscalYear == ris.FiscalYear && itemIds.Contains(a.ItemId))
            .ToDictionaryAsync(a => a.ItemId, a => (a.AllocatedQty, a.IssuedQty));
        var otherPending = await db.RisItems
            .Where(i => itemIds.Contains(i.ItemId) && i.RisId != ris.RisId)
            .Join(db.RisTransactions.Where(r => sharing.Contains(r.OfficeId) && r.FiscalYear == ris.FiscalYear && r.Status == RisStatus.Pending),
                  i => i.RisId, r => r.RisId, (i, r) => i)
            .GroupBy(i => i.ItemId)
            .Select(g => new { g.Key, Qty = g.Sum(i => i.RequestedQty) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
        var reserved = await ReservedByItemAsync(db, itemIds, exceptRisId: ris.RisId);

        return itemIds.Distinct().ToDictionary(id => id, id =>
        {
            var s = stock.GetValueOrDefault(id);
            var hasAlloc = alloc.TryGetValue(id, out var a);
            return new LineContext(s.StockOnHand, s.ReorderLevel,
                hasAlloc ? a.AllocatedQty : null, hasAlloc ? a.IssuedQty : null,
                otherPending.GetValueOrDefault(id), reserved.GetValueOrDefault(id));
        });
    }

    // Reserved stock (approved, waiting for pickup)
    // Lines of approved requisitions whose items are still in the stockroom.
    // Requisitions approved before I moved the stock deduction to "issue time" already had their stock
    // taken when approved (they have an ISSUE movement), so I don't count them again.
    private static IQueryable<RisItem> AwaitingPickup(AppDbContext db) =>
        db.RisItems.Where(i => i.IssuedQty > 0
            && db.RisTransactions.Any(r => r.RisId == i.RisId && r.Status == RisStatus.Approved)
            && !db.StockMovements.Any(m => m.RisId == i.RisId && m.ItemId == i.ItemId && m.MovementType == "ISSUE"));

    private static async Task<Dictionary<int, int>> ReservedByItemAsync(AppDbContext db, IReadOnlyCollection<int>? itemIds = null, int? exceptRisId = null)
    {
        var q = AwaitingPickup(db);
        if (itemIds is not null) q = q.Where(i => itemIds.Contains(i.ItemId));
        if (exceptRisId is not null) q = q.Where(i => i.RisId != exceptRisId);
        return await q.GroupBy(i => i.ItemId)
            .Select(g => new { g.Key, Qty = g.Sum(i => i.IssuedQty ?? 0) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
    }

    /// Reserved quantity per item, for the Inventory page.
    public async Task<Dictionary<int, int>> ReservedByItemAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ReservedByItemAsync(db);
    }

    public async Task<List<RoPersonnel>> PersonnelOfOfficeAsync(int officeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RoPersonnel.AsNoTracking().Where(p => p.OfficeId == officeId).OrderBy(p => p.FullName).ToListAsync();
    }

    public async Task<List<RoPersonnel>> AmsPersonnelAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RoPersonnel.AsNoTracking()
            .Where(p => db.Offices.Any(o => o.OfficeId == p.OfficeId && o.OfficeAcronym == "AMS"))
            .OrderBy(p => p.FullName).ToListAsync();
    }

    // Approve: use up the APP-CSE and reserve the stock, all or nothing
    public async Task<OpResult> ApproveAsync(uint adminUserId, int risId, IReadOnlyDictionary<int, LineDecision> decisions,
                                             int? approvedByPersonnelId, int? issuedByPersonnelId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT ris_id FROM ris_transactions WHERE ris_id = {risId} FOR UPDATE");
        var ris = await db.RisTransactions.Include(r => r.Items).FirstOrDefaultAsync(r => r.RisId == risId);
        if (ris is null) return OpResult.Fail("This requisition no longer exists.");
        if (ris.Status != RisStatus.Pending)
            return OpResult.Fail($"{ris.RisNo} is already {RisStatus.Label(ris.Status).ToLowerInvariant()}. Reload the page.");

        // Same lock per office that e-SRS uses when an office submits (on the office whose APP-CSE is used).
        var budgetOfficeId = (await AppCseScope.ForAsync(db, ris.OfficeId)).BudgetOfficeId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT office_id FROM offices WHERE office_id = {budgetOfficeId} FOR UPDATE");

        // The admin toggle "Require APP-CSE for Requisitions". When off, I still reserve warehouse stock
        // but don't block or cap on the APP-CSE balance (the office may request items outside its APP-CSE).
        var requireAppCse = await RequireAppCseAsync(db);

        var now = PhTime.Now;
        var problems = new List<string>();

        foreach (var line in ris.Items.OrderBy(i => i.ItemId))   // always the same order so there are no deadlocks
        {
            var d = decisions.GetValueOrDefault(line.RisItemId, new LineDecision(line.RequestedQty, null));
            if (d.IssueQty < 0 || d.IssueQty > line.RequestedQty)
                return OpResult.Fail($"Line {line.LineNo}: the issue quantity must be between 0 and {line.RequestedQty}.");

            line.IssuedQty = d.IssueQty;
            line.StockAvailable = d.IssueQty > 0;
            line.Remarks = string.IsNullOrWhiteSpace(d.Remarks)
                ? (d.IssueQty == 0 ? "Not available" : d.IssueQty < line.RequestedQty ? "Partial issue" : null)
                : d.Remarks.Trim()[..Math.Min(d.Remarks.Trim().Length, 255)];
            if (d.IssueQty == 0) continue;

            // Reserve only. The stock on hand isn't touched until the RIS is marked as issued.
            // Locking the item row stops two approvals from promising the same items.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT item_id FROM supply_items WHERE item_id = {line.ItemId} FOR UPDATE");
            var onHand = (int)await db.Database
                .SqlQuery<long>($"SELECT CAST(stock_on_hand AS SIGNED) AS Value FROM supply_items WHERE item_id = {line.ItemId}")
                .SingleAsync();
            var reservedElsewhere = (await ReservedByItemAsync(db, [line.ItemId], exceptRisId: ris.RisId)).GetValueOrDefault(line.ItemId);
            var available = Math.Max(onHand - reservedElsewhere, 0);
            if (available < d.IssueQty)
            {
                problems.Add(reservedElsewhere > 0
                    ? $"{line.StockNo}: only {available} available ({onHand} on hand, {reservedElsewhere} already approved for pickup by other offices)."
                    : $"{line.StockNo}: not enough in the warehouse for {d.IssueQty} (on hand: {onHand}).");
                continue;
            }

            if (requireAppCse)
            {
                var quotaOk = await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE app_cse_allocations SET issued_qty = issued_qty + {d.IssueQty}
                     WHERE office_id = {budgetOfficeId} AND item_id = {line.ItemId}
                       AND fiscal_year = {ris.FiscalYear} AND issued_qty + {d.IssueQty} <= allocated_qty");
                if (quotaOk == 0) { problems.Add($"{line.StockNo}: more than the office's remaining APP-CSE."); continue; }
            }
            // When APP-CSE is off I leave app_cse_allocations untouched: the office may be requesting
            // outside its APP-CSE, and pushing issued_qty past allocated_qty would break the CHECK
            // constraint. Only the warehouse stock (reserved above) controls the issue.
        }

        if (problems.Count > 0)
        {
            await tx.RollbackAsync();
            return OpResult.Fail("Nothing was approved. Lower these quantities and try again:", problems);
        }
        if (ris.Items.All(i => i.IssuedQty == 0))
            return OpResult.Fail("Every line is set to 0. Reject the requisition instead, with a reason for the office.");

        ris.Status = RisStatus.Approved;
        ris.ApprovedByUserId = adminUserId;
        ris.ApprovedAt = now;
        if (approvedByPersonnelId is not null) ris.ApprovedByPersonnelId = approvedByPersonnelId;
        // "Issued by" is printed on the slip the office brings to AMS, so I fill it now (default from settings).
        ris.IssuedByPersonnelId = issuedByPersonnelId ?? ris.IssuedByPersonnelId ?? await DefaultIssuerAsync(db);

        var partial = ris.Items.Count(i => i.IssuedQty < i.RequestedQty);
        db.RisStatusHistory.Add(new RisStatusHistory
        {
            RisId = ris.RisId, FromStatus = RisStatus.Pending, ToStatus = RisStatus.Approved, ChangedBy = adminUserId, ChangedAt = now,
            Note = partial == 0 ? "Approved in full." : $"Approved; {partial} line(s) issued partially or not at all.",
        });
        db.UserActivities.Add(new UserActivity { UserId = adminUserId, ActivityType = "Update", Description = $"Approved RIS {ris.RisNo}", CreatedAt = now });

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        log.LogInformation("RIS {RisNo} approved by user {UserId}", ris.RisNo, adminUserId);
        return OpResult.Success($"{ris.RisNo} is approved. The stock is reserved and will be deducted when you mark it as issued.");
    }

    // Reject (the reserved quantity is released automatically)
    public async Task<OpResult> RejectAsync(uint adminUserId, int risId, string reason)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length is < 10 or > 1000)
            return OpResult.Fail("Write a reason of at least 10 characters so the office knows what to fix.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT ris_id FROM ris_transactions WHERE ris_id = {risId} FOR UPDATE");

        var ris = await db.RisTransactions.FirstOrDefaultAsync(r => r.RisId == risId);
        if (ris is null) return OpResult.Fail("This requisition no longer exists.");
        if (ris.Status != RisStatus.Pending)
            return OpResult.Fail($"{ris.RisNo} is already {RisStatus.Label(ris.Status).ToLowerInvariant()}.");

        var now = PhTime.Now;
        ris.Status = RisStatus.Rejected;
        ris.RejectionReason = reason;
        ris.RejectedByUserId = adminUserId;
        ris.RejectedAt = now;
        db.RisStatusHistory.Add(new RisStatusHistory { RisId = risId, FromStatus = RisStatus.Pending, ToStatus = RisStatus.Rejected, ChangedBy = adminUserId, Note = reason, ChangedAt = now });
        db.UserActivities.Add(new UserActivity { UserId = adminUserId, ActivityType = "Update", Description = $"Rejected RIS {ris.RisNo}", CreatedAt = now });

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success($"{ris.RisNo} was rejected. The office will see your reason.");
    }

    // Signed copy
    public async Task<(string FileName, int Size, DateTime UploadedAt)?> SignedCopyInfoAsync(int risId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var info = await db.RisSignedCopies.AsNoTracking().Where(c => c.RisId == risId)
            .Select(c => new { c.OriginalFileName, c.SizeBytes, c.UploadedAt }).FirstOrDefaultAsync();
        return info is null ? null : (info.OriginalFileName, info.SizeBytes, info.UploadedAt);
    }

    private static async Task<int?> DefaultIssuerAsync(AppDbContext db)
    {
        var value = await db.SystemSettings.Where(s => s.SettingKey == "ris.issued_by_personnel_id")
            .Select(s => s.SettingValue).FirstOrDefaultAsync();
        return int.TryParse(value, out var id) && await db.RoPersonnel.AnyAsync(p => p.PersonnelId == id) ? id : null;
    }

    // Release: items handed over to the office
    public const int MaxSignedCopyBytes = 20 * 1024 * 1024;

    public async Task<OpResult> MarkIssuedAsync(uint adminUserId, int risId, int receivedByPersonnelId, int? issuedByPersonnelId,
                                                DateTime issuedOn, byte[] signedPdf, string fileName)
    {
        if (signedPdf.Length == 0) return OpResult.Fail("Attach the signed RIS (PDF) first.");
        if (signedPdf.Length > MaxSignedCopyBytes) return OpResult.Fail("The PDF is larger than 20 MB. Scan it at a lower resolution.");
        if (signedPdf.Length < 5 || signedPdf[0] != '%' || signedPdf[1] != 'P' || signedPdf[2] != 'D' || signedPdf[3] != 'F')
            return OpResult.Fail("That file isn't a PDF. Scan the signed RIS as a PDF and try again.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT ris_id FROM ris_transactions WHERE ris_id = {risId} FOR UPDATE");

        var ris = await db.RisTransactions.Include(r => r.Items).FirstOrDefaultAsync(r => r.RisId == risId);
        if (ris is null) return OpResult.Fail("This requisition no longer exists.");
        if (ris.Status != RisStatus.Approved)
            return OpResult.Fail($"Only approved requisitions can be released. {ris.RisNo} is {RisStatus.Label(ris.Status).ToLowerInvariant()}.");
        if (issuedOn.Date > PhTime.Now.Date) return OpResult.Fail("The release date can't be in the future.");
        if (ris.ApprovedAt is { } approvedAt && issuedOn.Date < approvedAt.Date) return OpResult.Fail("The release date can't be before the approval date.");

        var now = PhTime.Now;

        // The items leave the stockroom now, so I deduct the stock on hand and log it on each stock card.
        // Lines that already have an ISSUE movement were approved before this change, back when stock
        // was taken at approval, so I don't deduct them twice.
        var alreadyDeducted = await db.StockMovements
            .Where(m => m.RisId == risId && m.MovementType == "ISSUE").Select(m => m.ItemId).ToListAsync();
        var shortages = new List<string>();
        foreach (var line in ris.Items.Where(i => i.IssuedQty > 0).OrderBy(i => i.ItemId))   // always the same order so there are no deadlocks
        {
            if (alreadyDeducted.Contains(line.ItemId)) continue;
            var qty = line.IssuedQty!.Value;
            var stockOk = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE supply_items SET stock_on_hand = stock_on_hand - {qty}
                 WHERE item_id = {line.ItemId} AND stock_on_hand >= {qty}");
            if (stockOk == 0)
            {
                var onHand = (int)await db.Database
                    .SqlQuery<long>($"SELECT CAST(stock_on_hand AS SIGNED) AS Value FROM supply_items WHERE item_id = {line.ItemId}")
                    .SingleAsync();
                shortages.Add($"{line.StockNo}: needs {qty} {line.UnitOfMeasure}, but Inventory shows only {onHand}.");
                continue;
            }
            var balanceAfter = (int)await db.Database
                .SqlQuery<long>($"SELECT CAST(stock_on_hand AS SIGNED) AS Value FROM supply_items WHERE item_id = {line.ItemId}")
                .SingleAsync();
            db.StockMovements.Add(new StockMovement
            {
                ItemId = line.ItemId, MovementType = "ISSUE", Quantity = qty, BalanceAfter = balanceAfter,
                UnitCost = line.UnitCost, ReferenceType = "RIS", ReferenceNo = ris.RisNo,
                RisId = ris.RisId, OfficeId = ris.OfficeId, PerformedBy = adminUserId, CreatedAt = now,
            });
        }
        if (shortages.Count > 0)
        {
            await tx.RollbackAsync();
            return OpResult.Fail("Nothing was saved. The stockroom count is lower than this RIS. Correct it in Inventory (stock in), then try again:", shortages);
        }

        ris.Status = RisStatus.Issued;
        ris.ReceivedByPersonnelId = receivedByPersonnelId;
        if (issuedByPersonnelId is not null) ris.IssuedByPersonnelId = issuedByPersonnelId;
        ris.IssuedAt = issuedOn.Date == now.Date ? now : issuedOn.Date.AddHours(12);

        var name = Path.GetFileName(fileName);
        name = name[..Math.Min(name.Length, 255)];
        var copy = await db.RisSignedCopies.FindAsync(risId);
        if (copy is null)
        {
            copy = new RisSignedCopy { RisId = risId };
            db.RisSignedCopies.Add(copy);
        }
        copy.OriginalFileName = name;
        copy.Content = signedPdf;
        copy.SizeBytes = signedPdf.Length;
        copy.UploadedBy = adminUserId;
        copy.UploadedAt = now;
        db.RisStatusHistory.Add(new RisStatusHistory { RisId = risId, FromStatus = RisStatus.Approved, ToStatus = RisStatus.Issued, ChangedBy = adminUserId, Note = "Items released to the office.", ChangedAt = now });
        db.UserActivities.Add(new UserActivity { UserId = adminUserId, ActivityType = "Update", Description = $"Released RIS {ris.RisNo}", CreatedAt = now });

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success($"{ris.RisNo} is marked as issued. The stock on hand was deducted.");
    }
}

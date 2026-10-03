// RequisitionAdminService.cs — the AMS side of the RIS workflow:
//   PendingApproval → (approve) ApprovedForIssuance → (release) Issued
//                   ↘ (reject)  Rejected
// Approval deducts warehouse stock and the office's APP-CSE in one transaction,
// using the same row locks as the AMS Supplies portal, so the two sites can't
// double-spend the same stock or balance.

using System.Data;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed record OpResult(bool Ok, string Message, IReadOnlyList<string>? Details = null)
{
    /// Id of a record that was just created (when relevant).
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

/// What AMS needs to see beside each line before approving.
public sealed record LineContext(int StockOnHand, int ReorderLevel, int? Allocated, int? AlreadyIssued, int OtherPending)
{
    /// APP-CSE left for this line once this request is counted (null = item not in APP-CSE).
    public int? RemainingIncludingThis(int requested) =>
        Allocated is null ? null : Allocated.Value - (AlreadyIssued ?? 0) - OtherPending - requested;
}

public sealed record LineDecision(int IssueQty, string? Remarks);

public sealed class RequisitionAdminService(IDbContextFactory<AppDbContext> dbFactory, ILogger<RequisitionAdminService> log)
{
    // ── Queries ────────────────────────────────────────────────────────
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

        // Oldest pending first (first come, first served); everything else newest first.
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

    /// Live stock and APP-CSE figures for every line of a RIS (keyed by ItemId).
    public async Task<Dictionary<int, LineContext>> LineContextAsync(RisTransaction ris)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var itemIds = ris.Items.Select(i => i.ItemId).ToList();

        var stock = await db.SupplyItems.AsNoTracking().Where(i => itemIds.Contains(i.ItemId))
            .ToDictionaryAsync(i => i.ItemId, i => (i.StockOnHand, i.ReorderLevel));
        var alloc = await db.AppCseAllocations.AsNoTracking()
            .Where(a => a.OfficeId == ris.OfficeId && a.FiscalYear == ris.FiscalYear && itemIds.Contains(a.ItemId))
            .ToDictionaryAsync(a => a.ItemId, a => (a.AllocatedQty, a.IssuedQty));
        var otherPending = await db.RisItems
            .Where(i => itemIds.Contains(i.ItemId) && i.RisId != ris.RisId)
            .Join(db.RisTransactions.Where(r => r.OfficeId == ris.OfficeId && r.FiscalYear == ris.FiscalYear && r.Status == RisStatus.Pending),
                  i => i.RisId, r => r.RisId, (i, r) => i)
            .GroupBy(i => i.ItemId)
            .Select(g => new { g.Key, Qty = g.Sum(i => i.RequestedQty) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);

        return itemIds.Distinct().ToDictionary(id => id, id =>
        {
            var s = stock.GetValueOrDefault(id);
            var hasAlloc = alloc.TryGetValue(id, out var a);
            return new LineContext(s.StockOnHand, s.ReorderLevel,
                hasAlloc ? a.AllocatedQty : null, hasAlloc ? a.IssuedQty : null,
                otherPending.GetValueOrDefault(id));
        });
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

    // ── Approve: deduct stock + APP-CSE, all or nothing ────────────────
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

        // Same per-office lock the portal takes when an office submits.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT office_id FROM offices WHERE office_id = {ris.OfficeId} FOR UPDATE");

        var now = PhTime.Now;
        var problems = new List<string>();

        foreach (var line in ris.Items.OrderBy(i => i.ItemId))   // fixed order → no deadlocks
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

            var stockOk = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE supply_items SET stock_on_hand = stock_on_hand - {d.IssueQty}
                 WHERE item_id = {line.ItemId} AND stock_on_hand >= {d.IssueQty}");
            if (stockOk == 0) { problems.Add($"{line.StockNo}: not enough in the warehouse for {d.IssueQty}."); continue; }

            var quotaOk = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE app_cse_allocations SET issued_qty = issued_qty + {d.IssueQty}
                 WHERE office_id = {ris.OfficeId} AND item_id = {line.ItemId}
                   AND fiscal_year = {ris.FiscalYear} AND issued_qty + {d.IssueQty} <= allocated_qty");
            if (quotaOk == 0) { problems.Add($"{line.StockNo}: more than the office's remaining APP-CSE."); continue; }

            var balanceAfter = (int)await db.Database
                .SqlQuery<long>($"SELECT CAST(stock_on_hand AS SIGNED) AS Value FROM supply_items WHERE item_id = {line.ItemId}")
                .SingleAsync();

            db.StockMovements.Add(new StockMovement
            {
                ItemId = line.ItemId, MovementType = "ISSUE", Quantity = d.IssueQty, BalanceAfter = balanceAfter,
                UnitCost = line.UnitCost, ReferenceType = "RIS", ReferenceNo = ris.RisNo,
                RisId = ris.RisId, OfficeId = ris.OfficeId, PerformedBy = adminUserId, CreatedAt = now,
            });
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
        // "Issued by" is printed on the slip the office takes to AMS, so fill it now (default from settings).
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
        return OpResult.Success($"{ris.RisNo} is approved for issuance.");
    }

    // ── Reject (the pending reservation is released automatically) ────
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

    // ── Signed copy ──────────────────────────────────────────────────────
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

    // ── Release: items handed over to the office ──────────────────────
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

        var ris = await db.RisTransactions.FirstOrDefaultAsync(r => r.RisId == risId);
        if (ris is null) return OpResult.Fail("This requisition no longer exists.");
        if (ris.Status != RisStatus.Approved)
            return OpResult.Fail($"Only approved requisitions can be released. {ris.RisNo} is {RisStatus.Label(ris.Status).ToLowerInvariant()}.");
        if (issuedOn.Date > PhTime.Now.Date) return OpResult.Fail("The release date can't be in the future.");
        if (ris.ApprovedAt is { } approvedAt && issuedOn.Date < approvedAt.Date) return OpResult.Fail("The release date can't be before the approval date.");

        var now = PhTime.Now;
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
        return OpResult.Success($"{ris.RisNo} is marked as issued.");
    }
}

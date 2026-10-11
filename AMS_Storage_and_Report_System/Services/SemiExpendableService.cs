// SemiExpendableService.cs — SPLV / SPHV deliveries and their ICS / PAR numbers.
//
// Numbering (assigned when a delivery is saved, inside one transaction):
//   • SPLV items get an ICS no.; SPHV items get an ICS no. below ₱50,000, a PAR no. at ₱50,000 and up.
//   • Number = year-month delivered-running no., e.g. 2026-01-001; a line of 7 units takes 7
//     numbers: 2026-01-001-007.
//   • The running number keeps counting through the year (2026-01-050 → 2026-02-051) and starts
//     again at 001 in a new year. SPLV ICS, SPHV ICS and SPHV PAR each have their own count.

using System.Data;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed class SeLineForm
{
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "pc";
    public int Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    /// End user: office id (End users = Office) or personnel id (End users = RO-Personnel); 0 = not chosen.
    public int EndUserId { get; set; }
    public decimal Total => Math.Max(Quantity, 0) * Math.Max(UnitCost, 0);
}

public sealed class SeDeliveryForm
{
    public string Category { get; set; } = SeCategory.Splv;
    public DateTime DeliveredOn { get; set; } = PhTime.Now.Date;
    public int SupplierId { get; set; }
    /// null (not recorded), "Office" or "Personnel".
    public string? EndUserType { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
    public List<SeLineForm> Lines { get; set; } = [new()];
    public decimal Total => Lines.Sum(l => l.Total);
}

public sealed record SeSeriesInfo(int Year, string Category, string DocType, int LastSeq, int HighestAssigned);

public sealed record SeFilter(int Year, string? Category, string? Search);

public sealed class SemiExpendableService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxQuantity = 9_999;

    // ── Reading ─────────────────────────────────────────────────────────
    public async Task<List<SeDelivery>> ListAsync(SeFilter f)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.SeDeliveries.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.DeliveredOn.Year == f.Year);
        if (f.Category is SeCategory.Splv or SeCategory.Sphv) q = q.Where(d => d.Category == f.Category);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(d => d.SupplierName.Contains(s) || (d.ReferenceNo != null && d.ReferenceNo.Contains(s))
                          || d.Lines.Any(l => l.Description.Contains(s) || l.DocNo.Contains(s) || (l.EndUser != null && l.EndUser.Contains(s))));
        }
        var list = await q.OrderByDescending(d => d.DeliveredOn).ThenByDescending(d => d.DeliveryId).ToListAsync();
        foreach (var d in list) d.Lines = d.Lines.OrderBy(l => l.LineNo).ToList();
        return list;
    }

    public async Task<SeDelivery?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await db.SeDeliveries.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.DeliveryId == id);
        if (d is not null) d.Lines = d.Lines.OrderBy(l => l.LineNo).ToList();
        return d;
    }

    public async Task<List<int>> YearsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var years = await db.SeDeliveries.Select(d => d.DeliveredOn.Year).Distinct().ToListAsync();
        years.Add(PhTime.Now.Year);
        return years.Distinct().OrderByDescending(y => y).ToList();
    }

    /// Last number used in each series of a year, and the highest number actually on a delivery.
    public async Task<List<SeSeriesInfo>> SeriesAsync(int year)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var last = await db.SeNumberSeries.AsNoTracking().Where(s => s.DocYear == year).ToListAsync();
        var highest = await db.SeDeliveryLines.AsNoTracking().Where(l => l.DocYear == year)
            .GroupBy(l => new { l.Category, l.DocType })
            .Select(g => new { g.Key.Category, g.Key.DocType, Max = g.Max(l => l.SeqEnd) }).ToListAsync();
        return SeCategory.Series.Select(s => new SeSeriesInfo(year, s.Category, s.DocType,
                last.FirstOrDefault(x => x.Category == s.Category && x.DocType == s.DocType)?.LastSeq ?? 0,
                highest.FirstOrDefault(x => x.Category == s.Category && x.DocType == s.DocType)?.Max ?? 0))
            .ToList();
    }

    /// The numbers a delivery would get if it were saved now (for the form; final numbers are given on save).
    public static List<string> Preview(SeDeliveryForm f, IReadOnlyList<SeSeriesInfo> series)
    {
        var next = series.ToDictionary(s => (s.Category, s.DocType), s => s.LastSeq);
        var result = new List<string>();
        foreach (var l in f.Lines)
        {
            if (l.Quantity < 1) { result.Add(""); continue; }
            var type = SeCategory.DocTypeFor(f.Category, l.UnitCost);
            var key = (f.Category, type);
            var start = next.GetValueOrDefault(key) + 1;
            var end = start + l.Quantity - 1;
            next[key] = end;
            result.Add(SeCategory.Format(f.DeliveredOn.Year, f.DeliveredOn.Month, start, end));
        }
        return result;
    }

    // ── Saving ──────────────────────────────────────────────────────────
    public async Task<OpResult> SaveAsync(SeDeliveryForm f, uint userId)
    {
        if (f.Category is not (SeCategory.Splv or SeCategory.Sphv)) return OpResult.Fail("Choose SPLV or SPHV.");
        if (f.DeliveredOn.Date > PhTime.Now.Date) return OpResult.Fail("The delivery date can't be in the future.");
        if (f.DeliveredOn.Year < 2000) return OpResult.Fail("Check the delivery date.");
        if (f.SupplierId == 0) return OpResult.Fail("Choose the supplier who delivered the items.");
        var lines = f.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.UnitCost != 0).ToList();
        if (lines.Count == 0) return OpResult.Fail("Add at least one item.");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l.Description)) return OpResult.Fail($"Item {i + 1}: enter the description.");
            if (string.IsNullOrWhiteSpace(l.Unit)) return OpResult.Fail($"Item {i + 1}: enter the unit, for example pc, set or unit.");
            if (l.Quantity is < 1 or > MaxQuantity) return OpResult.Fail($"Item {i + 1}: the quantity must be between 1 and {MaxQuantity:N0}.");
            if (l.UnitCost <= 0) return OpResult.Fail($"Item {i + 1}: enter the unit price.");
            if (l.UnitCost > 99_999_999m) return OpResult.Fail($"Item {i + 1}: check the unit price.");
            if (f.EndUserType is not null && l.EndUserId == 0)
                return OpResult.Fail($"Item {i + 1}: choose the {(f.EndUserType == SeCategory.EndUserOffice ? "office" : "end user")}.");
        }
        if (f.EndUserType is not (null or SeCategory.EndUserOffice or SeCategory.EndUserPersonnel)) return OpResult.Fail("Choose Office or RO-Personnel for End users.");

        await using (var check = await dbFactory.CreateDbContextAsync())
        {
            var supplier = await check.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.SupplierId == f.SupplierId);
            if (supplier is null) return OpResult.Fail("That supplier no longer exists. Choose another one.");
            var year = f.DeliveredOn.Year;
            // Make sure each series row exists BEFORE the locked transaction (no lock is kept),
            // so two people saving at the same moment simply wait for each other.
            foreach (var type in lines.Select(l => SeCategory.DocTypeFor(f.Category, l.UnitCost)).Distinct())
                await check.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT IGNORE INTO se_number_series (doc_year, category, doc_type, last_seq) VALUES ({year}, {f.Category}, {type}, 0)");

            // End users, with the name as it is today (kept even if the office or person changes later)
            var names = new Dictionary<int, (int? OfficeId, int? PersonnelId, string Name)>();
            var ids = lines.Select(l => l.EndUserId).Where(id => id != 0).Distinct().ToList();
            if (f.EndUserType == SeCategory.EndUserOffice)
            {
                foreach (var o in await check.Offices.AsNoTracking().Where(o => ids.Contains(o.OfficeId)).ToListAsync())
                    names[o.OfficeId] = (o.OfficeId, null, string.IsNullOrWhiteSpace(o.OfficeAcronym) ? o.OfficeName : $"{o.OfficeAcronym} - {o.OfficeName}");
            }
            else if (f.EndUserType == SeCategory.EndUserPersonnel)
            {
                var people = await check.RoPersonnel.AsNoTracking().Where(p => ids.Contains(p.PersonnelId))
                    .Select(p => new { p.PersonnelId, p.OfficeId, p.FullName, Office = check.Offices.Where(o => o.OfficeId == p.OfficeId).Select(o => o.OfficeAcronym).FirstOrDefault() })
                    .ToListAsync();
                foreach (var p in people)
                    names[p.PersonnelId] = (p.OfficeId, p.PersonnelId, string.IsNullOrWhiteSpace(p.Office) ? p.FullName : $"{p.FullName} ({p.Office})");
            }
            if (f.EndUserType is not null && ids.FirstOrDefault(id => !names.ContainsKey(id)) is var missing && missing != 0)
                return OpResult.Fail("One of the chosen end users no longer exists. Choose again.");

            for (var attempt = 1; ; attempt++)
            {
                try { return await SaveOnceAsync(f, lines, supplier, names, userId); }
                catch (Exception ex) when (attempt < 4 && IsDeadlock(ex))
                {
                    await Task.Delay(50 * attempt);   // someone else was saving at the same moment; try again
                }
            }
        }
    }

    private static bool IsDeadlock(Exception ex) =>
        ex.GetBaseException() is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.LockDeadlock or MySqlConnector.MySqlErrorCode.LockWaitTimeout };

    private async Task<OpResult> SaveOnceAsync(SeDeliveryForm f, List<SeLineForm> lines, Supplier supplier,
                                               Dictionary<int, (int? OfficeId, int? PersonnelId, string Name)> endUsers, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var year = f.DeliveredOn.Year;
        var types = lines.Select(l => SeCategory.DocTypeFor(f.Category, l.UnitCost)).Distinct().OrderBy(t => t).ToList();

        // Lock each series this delivery uses (always in the same order), so no two saves get the same number.
        var last = new Dictionary<string, int>();
        foreach (var type in types)
        {
            last[type] = (int)await db.Database
                .SqlQuery<long>($"SELECT CAST(last_seq AS SIGNED) AS Value FROM se_number_series WHERE doc_year = {year} AND category = {f.Category} AND doc_type = {type} FOR UPDATE")
                .SingleAsync();
        }

        var delivery = new SeDelivery
        {
            Category = f.Category,
            DeliveredOn = f.DeliveredOn.Date,
            SupplierId = supplier.SupplierId,
            SupplierName = supplier.SupplierName,
            EndUserType = f.EndUserType,
            ReferenceNo = Clip(f.ReferenceNo, 100),
            Remarks = Clip(f.Remarks, 500),
            CreatedBy = userId,
            CreatedAt = PhTime.Now,
        };
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var type = SeCategory.DocTypeFor(f.Category, l.UnitCost);
            var start = last[type] + 1;
            var end = start + l.Quantity - 1;
            last[type] = end;
            var who = f.EndUserType is not null && endUsers.TryGetValue(l.EndUserId, out var u) ? u : default;
            delivery.Lines.Add(new SeDeliveryLine
            {
                OfficeId = who.OfficeId,
                PersonnelId = who.PersonnelId,
                EndUser = who.Name is { Length: > 0 } n ? n[..Math.Min(n.Length, 250)] : null,
                LineNo = i + 1,
                Description = l.Description.Trim()[..Math.Min(l.Description.Trim().Length, 500)],
                Unit = l.Unit.Trim()[..Math.Min(l.Unit.Trim().Length, 30)],
                Quantity = l.Quantity,
                UnitCost = Math.Round(l.UnitCost, 2),
                Category = f.Category,
                DocType = type,
                DocYear = year,
                SeqStart = start,
                SeqEnd = end,
                DocNo = SeCategory.Format(year, f.DeliveredOn.Month, start, end),
            });
        }
        delivery.TotalAmount = delivery.Lines.Sum(l => l.TotalCost);
        db.SeDeliveries.Add(delivery);

        foreach (var (type, value) in last)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE se_number_series SET last_seq = {value} WHERE doc_year = {year} AND category = {f.Category} AND doc_type = {type}");

        // e.g. "ICS 2026-07-017-018 to 2026-07-019, PAR 2026-07-002"
        var numbers = string.Join(", ", delivery.Lines.GroupBy(l => l.DocType).Select(g =>
            g.Count() == 1 ? $"{g.Key} {g.First().DocNo}" : $"{g.Key} {g.First().DocNo} to {g.Last().DocNo}"));
        db.UserActivities.Add(new UserActivity
        {
            UserId = userId, ActivityType = "Create", CreatedAt = PhTime.Now,
            Description = $"Recorded {f.Category} delivery from {supplier.SupplierName}: {delivery.Lines.Count} item(s), {numbers}",
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success($"Saved. {delivery.Lines.Count} item(s) numbered {numbers}.") with { Id = delivery.DeliveryId };
    }

    /// Only the newest delivery of its series can be deleted, so the numbers stay unbroken;
    /// its numbers are then given out again.
    public async Task<OpResult> DeleteAsync(int deliveryId, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var d = await db.SeDeliveries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.DeliveryId == deliveryId);
        if (d is null) return OpResult.Fail("This delivery no longer exists.");

        foreach (var g in d.Lines.GroupBy(l => (l.DocYear, l.DocType)))
        {
            var (year, type) = g.Key;
            var row = await db.SeNumberSeries.FirstOrDefaultAsync(s => s.DocYear == year && s.Category == d.Category && s.DocType == type);
            var maxHere = g.Max(l => l.SeqEnd);
            var later = await db.SeDeliveryLines.AnyAsync(l => l.DocYear == year && l.Category == d.Category && l.DocType == type
                                                               && l.DeliveryId != d.DeliveryId && l.SeqStart > maxHere);
            if (later || (row is not null && row.LastSeq > maxHere))
                return OpResult.Fail($"Only the newest {d.Category} {type} delivery can be deleted, so the numbers stay in order. "
                                     + "Later deliveries already use numbers after this one.");
        }
        foreach (var g in d.Lines.GroupBy(l => (l.DocYear, l.DocType)))
        {
            var (year, type) = g.Key;
            var row = await db.SeNumberSeries.FirstOrDefaultAsync(s => s.DocYear == year && s.Category == d.Category && s.DocType == type);
            if (row is not null) row.LastSeq = g.Min(l => l.SeqStart) - 1;
        }
        db.SeDeliveries.Remove(d);
        db.UserActivities.Add(new UserActivity
        {
            UserId = userId, ActivityType = "Delete", CreatedAt = PhTime.Now,
            Description = $"Deleted {d.Category} delivery from {d.SupplierName} ({string.Join(", ", d.Lines.Select(l => l.DocNo))})",
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success("Delivery deleted. Its numbers will be used by the next delivery.");
    }

    /// Sets the last number already used (for numbers given out on paper before using this page).
    public async Task<OpResult> SetLastNumberAsync(int year, string category, string docType, int lastSeq, uint userId)
    {
        if (!SeCategory.Series.Contains((category, docType))) return OpResult.Fail("Unknown series.");
        if (lastSeq is < 0 or > 999_999) return OpResult.Fail("Enter a number from 0 to 999999.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var highest = await db.SeDeliveryLines.Where(l => l.DocYear == year && l.Category == category && l.DocType == docType)
            .MaxAsync(l => (int?)l.SeqEnd) ?? 0;
        if (lastSeq < highest)
            return OpResult.Fail($"{category} {docType} {year} already has deliveries up to no. {highest:000}. The last number can't be lower.");
        var row = await db.SeNumberSeries.FirstOrDefaultAsync(s => s.DocYear == year && s.Category == category && s.DocType == docType);
        if (row is null) db.SeNumberSeries.Add(row = new SeNumberSeries { DocYear = year, Category = category, DocType = docType });
        var old = row.LastSeq;
        row.LastSeq = lastSeq;
        db.UserActivities.Add(new UserActivity
        {
            UserId = userId, ActivityType = "Update", CreatedAt = PhTime.Now,
            Description = $"Set the last {category} {docType} no. for {year} from {old:000} to {lastSeq:000}",
        });
        await db.SaveChangesAsync();
        return OpResult.Success($"The next {category} {docType} no. in {year} will be {lastSeq + 1:000}.");
    }

    // ── Excel ───────────────────────────────────────────────────────────
    public async Task<byte[]?> DeliveryExcelAsync(int id, string entityName)
    {
        var d = await GetAsync(id);
        return d is null ? null : Excel([d], $"{d.Category} delivery, {d.DeliveredOn:MMMM d, yyyy}", entityName, single: true);
    }

    /// Oldest first, like a register.
    private async Task<List<SeDelivery>> ChronologicalAsync(SeFilter f) =>
        (await ListAsync(f)).OrderBy(d => d.DeliveredOn).ThenBy(d => d.DeliveryId).ToList();

    public async Task<byte[]> RegisterExcelAsync(SeFilter f, string entityName) =>
        Excel(await ChronologicalAsync(f), $"{(f.Category ?? "SPLV and SPHV")} deliveries, {f.Year}", entityName, single: false);

    private static byte[] Excel(List<SeDelivery> deliveries, string title, string entityName, bool single)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(single ? "Delivery" : "Deliveries");
        var navy = XLColor.FromHtml("#00007F");
        ws.Cell(1, 1).Value = "SEMI-EXPENDABLE PROPERTY DELIVERIES";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(navy);
        ws.Cell(2, 1).Value = string.IsNullOrWhiteSpace(entityName) ? "Asset Management Section" : $"{entityName} · Asset Management Section";
        ws.Cell(3, 1).Value = title;
        ws.Range(2, 1, 3, 1).Style.Font.SetFontColor(XLColor.FromHtml("#565F7A"));

        var r = 5;
        if (single)
        {
            var d = deliveries[0];
            void Field(string label, string value) { ws.Cell(r, 1).Value = label; ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 3).Value = value; r++; }
            Field("Type", SeCategory.Label(d.Category));
            Field("Date delivered", d.DeliveredOn.ToString("MMMM d, yyyy"));
            Field("Supplier", d.SupplierName);
            if (!string.IsNullOrWhiteSpace(d.ReferenceNo)) Field("DR / invoice no.", d.ReferenceNo!);
            if (!string.IsNullOrWhiteSpace(d.Remarks)) Field("Remarks", d.Remarks!);
            r++;
        }

        List<string> headers = single
            ? ["Item", "ICS / PAR", "ICS / PAR No.", "Description", "Unit", "Quantity", "Unit Cost", "Total Cost"]
            : ["Date Delivered", "Type", "Supplier", "DR / Invoice No.", "ICS / PAR", "ICS / PAR No.", "Description", "Unit", "Quantity", "Unit Cost", "Total Cost"];
        // "Office" / "End User" column right after the description, when end users were recorded
        var descIndex = single ? 3 : 6;
        var hasEndUser = deliveries.Any(d => d.Lines.Any(l => l.EndUser is not null));
        if (hasEndUser) headers.Insert(descIndex + 1, EndUserTitle(deliveries));
        for (var c = 0; c < headers.Count; c++) ws.Cell(r, c + 1).Value = headers[c];
        var head = ws.Range(r, 1, r, headers.Count);
        head.Style.Font.Bold = true; head.Style.Font.FontColor = XLColor.White; head.Style.Fill.BackgroundColor = navy;
        head.Style.Alignment.WrapText = true;
        var first = ++r;

        foreach (var d in deliveries)
            foreach (var l in d.Lines)
            {
                List<XLCellValue> row = single
                    ? [l.LineNo, l.DocType, l.DocNo, l.Description, l.Unit, l.Quantity, (double)l.UnitCost, (double)l.TotalCost]
                    : [d.DeliveredOn, d.Category, d.SupplierName, d.ReferenceNo ?? "", l.DocType, l.DocNo, l.Description, l.Unit, l.Quantity, (double)l.UnitCost, (double)l.TotalCost];
                if (hasEndUser) row.Insert(descIndex + 1, l.EndUser ?? "");
                for (var c = 0; c < row.Count; c++) ws.Cell(r, c + 1).Value = row[c];
                r++;
            }
        var cols = headers.Count;
        if (r > first)
        {
            ws.Range(first, cols - 1, r - 1, cols).Style.NumberFormat.Format = "#,##0.00";
            if (!single) ws.Range(first, 1, r - 1, 1).Style.NumberFormat.Format = "yyyy-mm-dd";
            ws.Range(first, cols - 2, r - 1, cols - 2).Style.NumberFormat.Format = "#,##0";   // quantity
            ws.Range(first - 1, 1, r - 1, cols).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(first - 1, 1, r - 1, cols).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Range(first, 1, r - 1, cols).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }
        ws.Cell(r, cols - 1).Value = "TOTAL";
        ws.Cell(r, cols).FormulaA1 = r > first ? $"SUM({ws.Cell(first, cols).Address}:{ws.Cell(r - 1, cols).Address})" : "0";
        ws.Cell(r, cols).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(r, cols - 1, r, cols).Style.Font.Bold = true;
        ws.Range(r, cols - 1, r, cols).Style.Border.TopBorder = XLBorderStyleValues.Double;

        ws.Columns().AdjustToContents(first - 1, Math.Max(r, first), 8, 60);
        var descCol = single ? 4 : 7;
        ws.Column(descCol).Width = Math.Max(ws.Column(descCol).Width, 40);
        ws.Column(descCol).Style.Alignment.WrapText = true;
        if (hasEndUser)
        {
            ws.Column(descCol + 1).Width = Math.Max(ws.Column(descCol + 1).Width, 28);
            ws.Column(descCol + 1).Style.Alignment.WrapText = true;
        }
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, single ? 8 : 14);
        ws.SheetView.FreezeRows(first - 1);
        ws.PageSetup.PageOrientation = single ? XLPageOrientation.Portrait : XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(first - 1, first - 1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── PDF ─────────────────────────────────────────────────────────────
    public async Task<byte[]?> DeliveryPdfAsync(int id, string entityName, string preparedBy)
    {
        var d = await GetAsync(id);
        return d is null ? null : Pdf([d], $"{d.Category} delivery, {d.DeliveredOn:MMMM d, yyyy}", entityName, true, preparedBy);
    }

    public async Task<byte[]> RegisterPdfAsync(SeFilter f, string entityName, string preparedBy) =>
        Pdf(await ChronologicalAsync(f), $"{(f.Category ?? "SPLV and SPHV")} deliveries, {f.Year}", entityName, false, preparedBy);

    private sealed record PdfCol(string Title, float Width, SimplePdf.Align Align);

    private static byte[] Pdf(List<SeDelivery> deliveries, string title, string entityName, bool single, string preparedBy)
    {
        var pdf = new SimplePdf(landscape: !single);
        const float margin = 36, fontSize = 8.5f, pad = 3.5f, lineH = 10.5f;
        var width = pdf.PageWidth - 2 * margin;
        var navy = (0f, 0f, 0.5f);
        var muted = (0.34f, 0.37f, 0.48f);

        List<PdfCol> cols = single
            ?
            [
                new("Item", 28, SimplePdf.Align.Center), new("ICS / PAR No.", 92, SimplePdf.Align.Left), new("Description", 0, SimplePdf.Align.Left),
                new("Unit", 34, SimplePdf.Align.Center), new("Qty", 30, SimplePdf.Align.Right), new("Unit Cost", 58, SimplePdf.Align.Right),
                new("Total Cost", 64, SimplePdf.Align.Right),
            ]
            :
            [
                new("Date", 52, SimplePdf.Align.Left), new("Type", 34, SimplePdf.Align.Center), new("Supplier", 110, SimplePdf.Align.Left),
                new("DR / Invoice", 64, SimplePdf.Align.Left), new("ICS / PAR No.", 96, SimplePdf.Align.Left), new("Description", 0, SimplePdf.Align.Left),
                new("Unit", 36, SimplePdf.Align.Center), new("Qty", 30, SimplePdf.Align.Right), new("Unit Cost", 60, SimplePdf.Align.Right),
                new("Total Cost", 68, SimplePdf.Align.Right),
            ];
        var descIndex = single ? 2 : 5;
        var hasEndUser = deliveries.Any(d => d.Lines.Any(l => l.EndUser is not null));
        if (hasEndUser) cols.Insert(descIndex + 1, new(EndUserTitle(deliveries), single ? 112 : 110, SimplePdf.Align.Left));
        var fixedW = cols.Sum(c => c.Width);
        cols = cols.Select(c => c.Width == 0 ? c with { Width = width - fixedW } : c).ToList();

        float y = 0;
        var page = 0;
        void Header(bool first, bool columns = true)
        {
            pdf.NewPage(); page++;
            y = margin;
            pdf.Text(margin, y + 12, "SEMI-EXPENDABLE PROPERTY DELIVERIES", 14, true, color: navy);
            pdf.Text(margin + width, y + 12, $"Page {page}", 8, false, SimplePdf.Align.Right, muted);
            y += 28;
            pdf.Text(margin, y, string.IsNullOrWhiteSpace(entityName) ? "Asset Management Section" : $"{entityName} · Asset Management Section", 9, false, color: muted);
            y += 12;
            pdf.Text(margin, y, title, 9, false, color: muted);
            y += 14;
            if (first && single)
            {
                var d = deliveries[0];
                y += 4;
                void Field(string label, string value)
                {
                    pdf.Text(margin, y, label, 9.5f, true);
                    foreach (var line in SimplePdf.Wrap(value, width - 110, 9.5f)) { pdf.Text(margin + 110, y, line, 9.5f); y += 12.5f; }
                }
                Field("Type", SeCategory.Label(d.Category));
                Field("Date delivered", d.DeliveredOn.ToString("MMMM d, yyyy"));
                Field("Supplier", d.SupplierName);
                if (!string.IsNullOrWhiteSpace(d.ReferenceNo)) Field("DR / invoice no.", d.ReferenceNo!);
                if (!string.IsNullOrWhiteSpace(d.Remarks)) Field("Remarks", d.Remarks!);
                y += 6;
            }
            if (!columns) { y += 6; return; }
            // Column titles
            var hh = 18f;
            pdf.Box(margin, y, width, hh, fill: navy);
            var x = margin;
            foreach (var c in cols)
            {
                var tx = c.Align switch { SimplePdf.Align.Right => x + c.Width - pad, SimplePdf.Align.Center => x + c.Width / 2, _ => x + pad };
                pdf.Text(tx, y + 12, c.Title, 8, true, c.Align, (1, 1, 1));
                x += c.Width;
            }
            y += hh;
        }

        Header(first: true);
        var bottom = pdf.PageHeight - margin - 30;
        var shade = false;
        foreach (var d in deliveries)
            foreach (var l in d.Lines)
            {
                List<string> values = single
                    ? [l.LineNo.ToString(), $"{l.DocType} {l.DocNo}", l.Description, l.Unit, l.Quantity.ToString("N0"), l.UnitCost.ToString("N2"), l.TotalCost.ToString("N2")]
                    : [d.DeliveredOn.ToString("yyyy-MM-dd"), d.Category, d.SupplierName, d.ReferenceNo ?? "", $"{l.DocType} {l.DocNo}", l.Description, l.Unit, l.Quantity.ToString("N0"), l.UnitCost.ToString("N2"), l.TotalCost.ToString("N2")];
                if (hasEndUser) values.Insert(descIndex + 1, l.EndUser ?? "");
                var wrapped = values.Select((v, i) => SimplePdf.Wrap(v, cols[i].Width - 2 * pad, fontSize)).ToList();
                var rowH = wrapped.Max(w => w.Count) * lineH + 2 * pad;
                if (y + rowH > bottom) { pdf.Line(margin, y, margin + width, y, .5f, .6f); Header(first: false); shade = false; }
                if (shade) pdf.Box(margin, y, width, rowH, fill: (0.95f, 0.96f, 0.99f));
                shade = !shade;
                var x = margin;
                for (var i = 0; i < cols.Count; i++)
                {
                    var c = cols[i];
                    var tx = c.Align switch { SimplePdf.Align.Right => x + c.Width - pad, SimplePdf.Align.Center => x + c.Width / 2, _ => x + pad };
                    for (var k = 0; k < wrapped[i].Count; k++) pdf.Text(tx, y + pad + 8 + k * lineH, wrapped[i][k], fontSize, false, c.Align);
                    x += c.Width;
                }
                y += rowH;
                pdf.Line(margin, y, margin + width, y, .3f, .8f);
            }

        // Totals
        if (y + 60 > bottom) Header(first: false, columns: false);
        var total = deliveries.Sum(d => d.Lines.Sum(l => l.TotalCost));
        var units = deliveries.Sum(d => d.Lines.Sum(l => l.Quantity));
        pdf.Line(margin, y + 1, margin + width, y + 1, 1f);
        y += 15;
        pdf.Text(margin + pad, y, $"{deliveries.Sum(d => d.Lines.Count)} item(s), {units:N0} unit(s)", 9, false, color: muted);
        var amount = $"PHP {total:N2}";
        pdf.Text(margin + width - pad, y, amount, 9.5f, true, SimplePdf.Align.Right);
        pdf.Text(margin + width - pad - SimplePdf.Measure(amount, 9.5f, true) - 14, y, "TOTAL", 9.5f, true, SimplePdf.Align.Right);

        // Signatures (one delivery) or prepared-by line
        y += 40;
        if (single && y + 60 < bottom)
        {
            var colW = width / 3;
            string[] labels = ["Prepared by:", "Inspected by:", "Received by:"];
            for (var i = 0; i < 3; i++)
            {
                var x = margin + i * colW;
                pdf.Text(x, y, labels[i], 9);
                pdf.Line(x, y + 30, x + colW - 24, y + 30, .6f);
                if (i == 0 && !string.IsNullOrWhiteSpace(preparedBy)) pdf.Text(x + (colW - 24) / 2, y + 27, preparedBy.ToUpperInvariant(), 9, true, SimplePdf.Align.Center);
                pdf.Text(x + (colW - 24) / 2, y + 41, "Signature over printed name", 7.5f, false, SimplePdf.Align.Center, muted);
            }
        }
        // Footer on the last page
        pdf.Text(margin, pdf.PageHeight - margin + 6, $"Generated {PhTime.Now:MMMM d, yyyy h:mm tt} by AMS StockWatch{(string.IsNullOrWhiteSpace(preparedBy) ? "" : " · " + preparedBy)}", 7.5f, false, color: muted);
        return pdf.Save(title);
    }

    /// "Office" when every delivery shown records offices, otherwise "End User".
    private static string EndUserTitle(List<SeDelivery> deliveries) =>
        deliveries.Where(d => d.EndUserType is not null).Select(d => d.EndUserType).Distinct().ToList() is [SeCategory.EndUserOffice]
            ? "Office" : "End User";

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
    }
}

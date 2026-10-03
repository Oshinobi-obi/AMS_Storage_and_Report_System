// AppCseService.cs — loads each office's APP-CSE from an Excel file.
// 1) Download the template (it already contains the current figures).
// 2) Fill in / correct quantities in Excel.
// 3) Upload: every row is checked and previewed; nothing is saved until "Import".

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed class AppCseRowCheck
{
    public int RowNumber { get; set; }
    public string OfficeAcronym { get; set; } = "";
    public string StockNo { get; set; } = "";
    public string? ItemName { get; set; }
    public int? Q1 { get; set; }
    public int? Q2 { get; set; }
    public int? Q3 { get; set; }
    public int? Q4 { get; set; }
    public int Total { get; set; }
    public int? CurrentTotal { get; set; }
    public int AlreadyUsed { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public int OfficeId { get; set; }
    public int ItemId { get; set; }

    public string Change => Error is not null ? "Error"
        : CurrentTotal is null ? "New"
        : CurrentTotal == Total ? "Unchanged" : "Changed";
}

public sealed class AppCsePreview
{
    public int FiscalYear { get; set; }
    public string FileName { get; set; } = "";
    public List<AppCseRowCheck> Rows { get; set; } = [];
    public string? FileError { get; set; }
    public int Errors => Rows.Count(r => r.Error is not null);
    public bool CanImport => FileError is null && Errors == 0 && Rows.Any(r => r.Change is "New" or "Changed");
}

public sealed class AppCseService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// Excel template: one row per requisitioning office × active item, filled with the current figures.
    public async Task<byte[]> TemplateAsync(int year)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var offices = await db.Offices.AsNoTracking().Where(o => o.CanRequisition && o.IsActive).OrderBy(o => o.OfficeAcronym).ToListAsync();
        var items = await db.SupplyItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.StockNo).ToListAsync();
        var current = await db.AppCseAllocations.AsNoTracking().Where(a => a.FiscalYear == year)
            .ToDictionaryAsync(a => (a.OfficeId, a.ItemId));

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add($"APP-CSE {year}");
        string[] headers = ["Office", "Stock No.", "Item", "Unit", "Q1", "Q2", "Q3", "Q4", "Total"];
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var header = ws.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#00007F");
        header.Style.Font.FontColor = XLColor.White;

        var r = 2;
        foreach (var o in offices)
            foreach (var i in items)
            {
                current.TryGetValue((o.OfficeId, i.ItemId), out var a);
                ws.Cell(r, 1).Value = o.OfficeAcronym;
                ws.Cell(r, 2).Value = i.StockNo;
                ws.Cell(r, 3).Value = string.IsNullOrWhiteSpace(i.Specifications) ? i.ItemName : $"{i.ItemName}, {i.Specifications}";
                ws.Cell(r, 4).Value = i.UnitOfMeasure;
                if (a?.Q1Qty is int q1) ws.Cell(r, 5).Value = q1;
                if (a?.Q2Qty is int q2) ws.Cell(r, 6).Value = q2;
                if (a?.Q3Qty is int q3) ws.Cell(r, 7).Value = q3;
                if (a?.Q4Qty is int q4) ws.Cell(r, 8).Value = q4;
                // Total adds up Q1–Q4; for yearly-only figures it keeps the stored total.
                var fallback = a is null || a.Q1Qty is not null ? "\"\"" : a.AllocatedQty.ToString();
                ws.Cell(r, 9).FormulaA1 = "IF(COUNT(E" + r + ":H" + r + ")>0,SUM(E" + r + ":H" + r + ")," + fallback + ")";
                r++;
            }

        ws.Range(2, 3, Math.Max(r - 1, 2), 4).Style.Font.FontColor = XLColor.FromHtml("#565F7A");
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        ws.Column(3).Width = Math.Min(ws.Column(3).Width, 60);

        var notes = wb.Worksheets.Add("How to fill in");
        notes.Cell(1, 1).Value = "How to fill in the APP-CSE";
        notes.Cell(1, 1).Style.Font.Bold = true;
        string[] lines =
        [
            "Fill in Q1 to Q4 for each office and item. Total adds them up by itself.",
            "If you only have yearly totals, leave Q1–Q4 blank and type the yearly quantity in Total instead.",
            "Leave a row blank (or 0) when the office doesn't need that item.",
            "Don't change the Office and Stock No. columns. Item and Unit are only for reference.",
            "You can delete rows you don't need. Save the file and upload it in AMS StockWatch → APP-CSE.",
        ];
        for (var i = 0; i < lines.Length; i++) notes.Cell(i + 3, 1).Value = $"{i + 1}. {lines[i]}";
        notes.Column(1).Width = 110;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// Reads and checks an uploaded file. Nothing is saved.
    public async Task<AppCsePreview> PreviewAsync(Stream file, string fileName, int year)
    {
        var preview = new AppCsePreview { FiscalYear = year, FileName = fileName };
        XLWorkbook wb;
        try { wb = new XLWorkbook(file); }
        catch { preview.FileError = "This file couldn't be opened. Save it as an Excel workbook (.xlsx) and try again."; return preview; }

        using (wb)
        {
            var ws = wb.Worksheets.First();
            var headerRow = ws.FirstRowUsed();
            if (headerRow is null) { preview.FileError = "The first sheet is empty."; return preview; }

            int Col(params string[] names) =>
                headerRow.CellsUsed().FirstOrDefault(c => names.Any(n => c.GetString().Trim().Equals(n, StringComparison.OrdinalIgnoreCase)))?.Address.ColumnNumber ?? 0;
            int cOffice = Col("Office", "Office Acronym"), cStock = Col("Stock No.", "Stock No", "Stock Number");
            int cQ1 = Col("Q1"), cQ2 = Col("Q2"), cQ3 = Col("Q3"), cQ4 = Col("Q4"), cTotal = Col("Total");
            if (cOffice == 0 || cStock == 0 || (cTotal == 0 && cQ1 == 0))
            {
                preview.FileError = "The first row must have the columns Office, Stock No. and either Q1–Q4 or Total. Download the template to see the layout.";
                return preview;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var offices = await db.Offices.AsNoTracking().ToDictionaryAsync(o => o.OfficeAcronym.ToUpperInvariant());
            var items = await db.SupplyItems.AsNoTracking().ToDictionaryAsync(i => i.StockNo.ToUpperInvariant());
            var current = await db.AppCseAllocations.AsNoTracking().Where(a => a.FiscalYear == year)
                .ToDictionaryAsync(a => (a.OfficeId, a.ItemId));
            var pending = await db.RisItems
                .Join(db.RisTransactions.Where(r => r.FiscalYear == year && r.Status == RisStatus.Pending), i => i.RisId, r => r.RisId, (i, r) => new { r.OfficeId, i.ItemId, i.RequestedQty })
                .GroupBy(x => new { x.OfficeId, x.ItemId })
                .Select(g => new { g.Key.OfficeId, g.Key.ItemId, Qty = g.Sum(x => x.RequestedQty) })
                .ToDictionaryAsync(x => (x.OfficeId, x.ItemId), x => x.Qty);

            var seen = new HashSet<(int, int)>();
            foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > headerRow.RowNumber()))
            {
                var officeText = row.Cell(cOffice).GetString().Trim();
                var stockText = row.Cell(cStock).GetString().Trim();
                if (officeText.Length == 0 && stockText.Length == 0) continue;

                var check = new AppCseRowCheck { RowNumber = row.RowNumber(), OfficeAcronym = officeText, StockNo = stockText };
                string? numberError = null;
                int? Read(int col)
                {
                    if (col == 0) return null;
                    var cell = row.Cell(col);
                    if (cell.IsEmpty()) return null;
                    if (cell.Value.IsNumber)
                    {
                        var d = cell.Value.GetNumber();
                        if (d < 0 || d != Math.Floor(d)) { numberError = "Quantities must be whole numbers, 0 or more."; return null; }
                        return (int)d;
                    }
                    var text = cell.GetFormattedString().Replace(",", "").Trim();
                    if (text.Length == 0) return null;
                    if (int.TryParse(text, out var n) && n >= 0) return n;
                    numberError = $"\"{text}\" isn't a valid quantity.";
                    return null;
                }
                check.Q1 = Read(cQ1); check.Q2 = Read(cQ2); check.Q3 = Read(cQ3); check.Q4 = Read(cQ4);
                var total = Read(cTotal);
                var hasQuarters = check.Q1 is not null || check.Q2 is not null || check.Q3 is not null || check.Q4 is not null;
                var quarterSum = (check.Q1 ?? 0) + (check.Q2 ?? 0) + (check.Q3 ?? 0) + (check.Q4 ?? 0);
                check.Total = hasQuarters ? quarterSum : total ?? 0;
                if (hasQuarters && total is int t && t != quarterSum)
                    check.Warning = $"Total says {t}, but Q1–Q4 add up to {quarterSum}. Q1–Q4 will be used.";

                if (!offices.TryGetValue(officeText.ToUpperInvariant(), out var office)) check.Error = $"Unknown office \"{officeText}\".";
                else if (!office.CanRequisition) check.Error = $"{office.OfficeAcronym} isn't allowed to requisition supplies.";
                else if (!items.TryGetValue(stockText.ToUpperInvariant(), out var item)) check.Error = $"Unknown stock no. \"{stockText}\". Add it to the catalog first.";
                else
                {
                    check.OfficeId = office.OfficeId;
                    check.ItemId = item.ItemId;
                    check.ItemName = item.ItemName;
                    current.TryGetValue((office.OfficeId, item.ItemId), out var existing);
                    check.CurrentTotal = existing?.AllocatedQty;
                    check.AlreadyUsed = (existing?.IssuedQty ?? 0) + pending.GetValueOrDefault((office.OfficeId, item.ItemId));

                    if (numberError is not null) check.Error = numberError;
                    else if (!seen.Add((office.OfficeId, item.ItemId))) check.Error = $"{office.OfficeAcronym} / {item.StockNo} appears more than once in the file.";
                    else if (check.Total < check.AlreadyUsed)
                        check.Error = $"{office.OfficeAcronym} has already used {check.AlreadyUsed} (issued or pending). The new total can't be lower.";
                }

                // A blank row for an item the office never had: nothing to do.
                if (check.Error is null && check.Total == 0 && check.CurrentTotal is null) continue;
                preview.Rows.Add(check);
            }

            if (preview.Rows.Count == 0 && preview.FileError is null)
                preview.FileError = "No quantities were found in the file.";
        }
        return preview;
    }

    /// Saves a preview that passed every check.
    public async Task<OpResult> ImportAsync(AppCsePreview preview, uint userId)
    {
        if (!preview.CanImport) return OpResult.Fail("Fix the rows marked as errors first.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var upload = new AppCseUpload
        {
            FiscalYear = preview.FiscalYear, OriginalFileName = preview.FileName[..Math.Min(preview.FileName.Length, 255)],
            FilePath = "", RowCount = preview.Rows.Count, UploadedBy = userId, UploadedAt = PhTime.Now,
            Notes = $"{preview.Rows.Count(r => r.Change == "New")} new, {preview.Rows.Count(r => r.Change == "Changed")} changed",
        };
        db.AppCseUploads.Add(upload);
        await db.SaveChangesAsync();

        var existing = await db.AppCseAllocations.Where(a => a.FiscalYear == preview.FiscalYear).ToDictionaryAsync(a => (a.OfficeId, a.ItemId));
        foreach (var r in preview.Rows.Where(r => r.Change is "New" or "Changed" || (r.Change == "Unchanged" && r.Q1 is not null)))
        {
            if (!existing.TryGetValue((r.OfficeId, r.ItemId), out var a))
            {
                a = new AppCseAllocation { OfficeId = r.OfficeId, ItemId = r.ItemId, FiscalYear = preview.FiscalYear };
                db.AppCseAllocations.Add(a);
            }
            a.AllocatedQty = r.Total;
            a.Q1Qty = r.Q1; a.Q2Qty = r.Q2; a.Q3Qty = r.Q3; a.Q4Qty = r.Q4;
            a.UploadId = upload.UploadId;
        }
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"Imported APP-CSE {preview.FiscalYear} from {preview.FileName}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return OpResult.Success($"APP-CSE {preview.FiscalYear} saved: {upload.Notes}. Offices see the new figures right away.");
    }

    public async Task<List<AppCseAllocation>> AllocationsAsync(int year, int officeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppCseAllocations.AsNoTracking().Include(a => a.Item).Include(a => a.Office)
            .Where(a => a.FiscalYear == year && (officeId == 0 || a.OfficeId == officeId))
            .OrderBy(a => a.Office!.OfficeAcronym).ThenBy(a => a.Item!.StockNo)
            .ToListAsync();
    }

    public async Task<List<AppCseUpload>> UploadsAsync(int year)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AppCseUploads.AsNoTracking().Where(u => u.FiscalYear == year).OrderByDescending(u => u.UploadedAt).Take(10).ToListAsync();
    }
}

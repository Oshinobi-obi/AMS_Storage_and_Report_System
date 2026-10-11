// AppCseService.cs — loads each office's APP-CSE from an Excel file.
// 1) Download the template (it already contains the current figures).
// 2) Fill in / correct quantities in Excel.
// 3) Upload: every row is checked and previewed; nothing is saved until "Import".

using System.Globalization;
using System.Text.RegularExpressions;
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
    /// The workbook sheet this row came from (official PS-DBM workbook only).
    public string? Sheet { get; set; }
    /// Set when the item isn't in the Catalog yet; it is created on import if allowed.
    public string? NewItemKey { get; set; }

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

    /// "template" (the website's own Excel) or "official" (the PS-DBM APP-CSE workbook).
    public string Format { get; set; } = "template";
    public List<AppCseSheetInfo> Sheets { get; set; } = [];
    public List<NewCatalogItem> NewItems { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    /// Add items that aren't in the Catalog yet (official workbook). If off, their rows are skipped.
    public bool AddMissingItems { get; set; } = true;

    public bool WillImport(AppCseRowCheck r) =>
        r.Error is null && (r.NewItemKey is null || AddMissingItems) &&
        (r.Change is "New" or "Changed" || (r.Change == "Unchanged" && r.Q1 is not null));

    public bool CanImport => FileError is null && Errors == 0 && Rows.Any(r => WillImport(r) && r.Change is "New" or "Changed");
}

public sealed class AppCseSheetInfo
{
    public string Sheet { get; set; } = "";
    public string? Office { get; set; }
    public int Items { get; set; }
    public int Units { get; set; }
    public int Warnings { get; set; }
    public string Status { get; set; } = "Read";      // Read | Skipped (empty) | Error | Check only
    public string? Note { get; set; }
}

public sealed class NewCatalogItem
{
    public string Key { get; set; } = "";
    public string StockNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
    public bool FromPartII { get; set; }
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
            // The Regional Office's official PS-DBM APP-CSE workbook (one sheet per office)?
            if (wb.Worksheets.Any(sheet => FindOfficialHeader(sheet) is not null))
                return await PreviewOfficialAsync(wb, preview);

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

        // Items from the workbook that aren't in the Catalog yet
        var newIds = new Dictionary<string, int>();
        var added = 0;
        if (preview.AddMissingItems && preview.NewItems.Count > 0)
        {
            var reorderText = await db.SystemSettings.Where(x => x.SettingKey == "stock.default_reorder").Select(x => x.SettingValue).FirstOrDefaultAsync();
            var reorder = int.TryParse(reorderText, out var rl) ? rl : 10;
            var categories = await db.ItemCategories.ToListAsync();
            var nextSort = categories.Count == 0 ? 1 : categories.Max(c => c.SortOrder) + 1;
            foreach (var n in preview.NewItems)
            {
                var already = await db.SupplyItems.FirstOrDefaultAsync(i => i.StockNo == n.StockNo);
                if (already is not null) { newIds[n.Key] = already.ItemId; continue; }

                var cat = categories.FirstOrDefault(c => c.CategoryName.Equals(n.Category, StringComparison.OrdinalIgnoreCase));
                if (cat is null && n.Category.Length > 0)
                {
                    cat = new ItemCategory { CategoryName = n.Category[..Math.Min(n.Category.Length, 100)], Icon = n.FromPartII ? "bi-bag" : "bi-box-seam", SortOrder = nextSort++ };
                    db.ItemCategories.Add(cat);
                    categories.Add(cat);
                    await db.SaveChangesAsync();
                }
                var item = new SupplyItem
                {
                    StockNo = n.StockNo, ItemName = n.Name[..Math.Min(n.Name.Length, 200)], UnitOfMeasure = n.Unit[..Math.Min(n.Unit.Length, 30)],
                    UnitPrice = n.Price, CategoryId = cat?.CategoryId, ReorderLevel = reorder, IsActive = true,
                };
                db.SupplyItems.Add(item);
                await db.SaveChangesAsync();
                newIds[n.Key] = item.ItemId;
                added++;
            }
        }

        var existing = await db.AppCseAllocations.Where(a => a.FiscalYear == preview.FiscalYear).ToDictionaryAsync(a => (a.OfficeId, a.ItemId));
        foreach (var r in preview.Rows.Where(preview.WillImport))
        {
            if (r.NewItemKey is not null) r.ItemId = newIds.GetValueOrDefault(r.NewItemKey);
            if (r.ItemId == 0) continue;
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
        var catalogNote = added > 0 ? $" {added} new item(s) were added to the Catalog." : "";
        return OpResult.Success($"APP-CSE {preview.FiscalYear} saved: {upload.Notes}.{catalogNote} Offices see the new figures right away.");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Official PS-DBM APP-CSE workbook (one sheet per office + a consolidated sheet)
    // ════════════════════════════════════════════════════════════════════════
    // Columns of the PS-DBM form: E–G, J–L, O–Q, T–V months · H, M, R, W quarters ·
    // Y total for the year · Z unit price. Rows: category headings, items, PART II, totals.
    private static readonly int[] MonthCols = [5, 6, 7, 10, 11, 12, 15, 16, 17, 20, 21, 22];
    private static readonly int[] QuarterCols = [8, 13, 18, 23];
    private const int TotalCol = 25, PriceCol = 26;

    private sealed record SheetRow(int Row, int Part, string? Code, string Name, string Unit, string Section,
                                   double[] Months, double[] Quarters, double Total, double Price, string? Error);

    private static string Text(IXLCell c)
    {
        var v = c.Value;
        var t = v.IsBlank ? "" : v.IsText ? v.GetText() : c.GetFormattedString();
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    /// Number in a cell, whether stored as a number or as text ("811.6"). Blank = 0.
    private static bool TryNumber(IXLCell c, out double value)
    {
        value = 0;
        var v = c.Value;
        if (v.IsBlank) return true;
        if (v.IsNumber) { value = v.GetNumber(); return true; }
        if (v.IsError) return false;
        var t = Text(c).Replace(",", "");
        return t.Length == 0 || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// Row of the "Item & Specifications" heading (row 2 in 2026, row 4 in 2027), or null if not this form.
    private static int? FindOfficialHeader(IXLWorksheet ws)
    {
        for (var r = 1; r <= 15; r++)
            if (Text(ws.Cell(r, 1)).StartsWith("Item & Spec", StringComparison.OrdinalIgnoreCase) &&
                Text(ws.Cell(r, TotalCol)).Contains("Total Quantity", StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    /// "13 ORD" → "ORD", "ORD" → "ORD"
    private static string SheetOffice(string sheetName) => Regex.Replace(sheetName, @"^\s*\d+[\s.\-_)]*", "").Trim();

    private static bool IsConsolidated(string sheetName)
    {
        var n = SheetOffice(sheetName).ToUpperInvariant();
        return n.Contains("CONSOL") || n is "FINAL" or "SUMMARY" or "TOTAL";
    }

    private static string NormalName(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToUpperInvariant();

    private static string TitleCase(string s)
    {
        var small = new HashSet<string> { "and", "or", "of", "the", "for", "in", "at", "to", "with" };
        var words = Regex.Replace(s, @"\(.*?\)", "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) => i > 0 && small.Contains(w) ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static List<SheetRow> ReadOfficialSheet(IXLWorksheet ws, int header)
    {
        var rows = new List<SheetRow>();
        var part = 1;
        var section = "";
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = header + 2; r <= last; r++)
        {
            var a = Text(ws.Cell(r, 1));
            var code = Text(ws.Cell(r, 2));
            var name = Text(ws.Cell(r, 3));
            if (a.StartsWith("PART II", StringComparison.OrdinalIgnoreCase)) { part = 2; section = "Other Items"; continue; }
            if (a.StartsWith("PART I", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(a, @"^[A-E]\.\s")) break;                       // "A. TOTAL", "B. ADDITIONAL PROVISION…"
            var isNumbered = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (!isNumbered)
            {
                if (a.Length > 0 && code.Length == 0 && name.Length == 0 && part == 1) section = TitleCase(a);
                continue;
            }
            if (name.Length == 0) continue;

            string? error = null;
            double Read(int col)
            {
                if (TryNumber(ws.Cell(r, col), out var v)) return v;
                error ??= $"Cell {ws.Cell(r, col).Address} has \"{Text(ws.Cell(r, col))}\" instead of a number.";
                return 0;
            }
            var months = MonthCols.Select(Read).ToArray();
            var quarters = QuarterCols.Select(Read).ToArray();
            var total = Read(TotalCol);
            TryNumber(ws.Cell(r, PriceCol), out var price);
            rows.Add(new SheetRow(r, part, code.Length > 0 ? code : null, name, Text(ws.Cell(r, 4)), section, months, quarters, total, price, error));
        }
        return rows;
    }

    private async Task<AppCsePreview> PreviewOfficialAsync(XLWorkbook wb, AppCsePreview preview)
    {
        preview.Format = "official";
        var year = preview.FiscalYear;
        await using var db = await dbFactory.CreateDbContextAsync();
        var offices = await db.Offices.AsNoTracking().ToListAsync();
        var bySheet = new Dictionary<string, Office>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in offices) bySheet.TryAdd(o.AppCseSheet ?? o.OfficeAcronym, o);
        foreach (var o in offices) bySheet.TryAdd(o.OfficeAcronym, o);

        var items = await db.SupplyItems.AsNoTracking().ToListAsync();
        var byCode = items.GroupBy(i => i.StockNo.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var byName = new Dictionary<string, SupplyItem>();
        foreach (var i in items)
        {
            byName.TryAdd(NormalName(i.ItemName), i);
            if (!string.IsNullOrWhiteSpace(i.Specifications)) byName.TryAdd(NormalName($"{i.ItemName}, {i.Specifications}"), i);
        }
        var current = await db.AppCseAllocations.AsNoTracking().Where(a => a.FiscalYear == year).ToDictionaryAsync(a => (a.OfficeId, a.ItemId));
        var pending = await db.RisItems
            .Join(db.RisTransactions.Where(r => r.FiscalYear == year && r.Status == RisStatus.Pending), i => i.RisId, r => r.RisId, (i, r) => new { r.OfficeId, i.ItemId, i.RequestedQty })
            .GroupBy(x => new { x.OfficeId, x.ItemId })
            .Select(g => new { g.Key.OfficeId, g.Key.ItemId, Qty = g.Sum(x => x.RequestedQty) })
            .ToDictionaryAsync(x => (x.OfficeId, x.ItemId), x => x.Qty);

        // Part II items get simple stock numbers (OTH-001, OTH-002, …) after the highest one in use
        var nextOther = items.Select(i => Regex.Match(i.StockNo, @"^OTH-(\d+)$", RegexOptions.IgnoreCase))
                             .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;
        var newItems = new Dictionary<string, NewCatalogItem>();
        var seenOffices = new Dictionary<int, string>();
        var sheetTotals = new Dictionary<string, double>();      // item key → offices' sheets added up
        List<SheetRow>? consolidated = null;
        string? consolidatedName = null;

        foreach (var ws in wb.Worksheets)
        {
            var header = FindOfficialHeader(ws);
            if (header is null) continue;                          // e.g. an empty "Sheet1"
            var rowsRead = ReadOfficialSheet(ws, header.Value);
            if (IsConsolidated(ws.Name))
            {
                consolidated = rowsRead;
                consolidatedName = ws.Name;
                preview.Sheets.Add(new AppCseSheetInfo { Sheet = ws.Name, Status = "Check only", Note = "Consolidated sheet: used only to compare totals, not imported." });
                continue;
            }

            var info = new AppCseSheetInfo { Sheet = ws.Name };
            preview.Sheets.Add(info);
            if (!rowsRead.Any(r => r.Total > 0 || r.Quarters.Any(q => q > 0) || r.Error is not null))
            {
                info.Status = "Skipped";
                info.Note = "No quantities on this sheet. The office keeps its current APP-CSE.";
                info.Office = bySheet.GetValueOrDefault(SheetOffice(ws.Name))?.OfficeAcronym;
                continue;
            }

            var officeName = SheetOffice(ws.Name);
            if (!bySheet.TryGetValue(officeName, out var office))
            {
                info.Status = "Error";
                info.Note = $"No office is called \"{officeName}\". Set the office's APP-CSE sheet name in Settings → Offices, then upload again.";
                preview.Rows.Add(new AppCseRowCheck { Sheet = ws.Name, OfficeAcronym = officeName, Error = info.Note });
                continue;
            }
            info.Office = office.OfficeAcronym;
            if (!office.CanRequisition)
            {
                info.Status = "Error";
                info.Note = $"{office.OfficeAcronym} isn't allowed to request supplies. Turn it on in Settings → Offices, or remove the sheet.";
                preview.Rows.Add(new AppCseRowCheck { Sheet = ws.Name, OfficeAcronym = office.OfficeAcronym, Error = info.Note });
                continue;
            }
            if (seenOffices.TryGetValue(office.OfficeId, out var otherSheet))
            {
                info.Status = "Error";
                info.Note = $"Sheets \"{otherSheet}\" and \"{ws.Name}\" are both {office.OfficeAcronym}.";
                preview.Rows.Add(new AppCseRowCheck { Sheet = ws.Name, OfficeAcronym = office.OfficeAcronym, Error = info.Note });
                continue;
            }
            seenOffices[office.OfficeId] = ws.Name;

            foreach (var r in rowsRead)
            {
                var key = r.Code is not null ? r.Code.ToUpperInvariant() : "P2:" + NormalName(r.Name);
                var check = new AppCseRowCheck { Sheet = ws.Name, RowNumber = r.Row, OfficeAcronym = office.OfficeAcronym, OfficeId = office.OfficeId, StockNo = r.Code ?? "", ItemName = r.Name };

                // Quarters are the official figures; a row with only a yearly total is imported as a yearly total.
                var hasQuarters = r.Quarters.Any(q => q > 0);
                var total = hasQuarters ? r.Quarters.Sum() : r.Total;
                if (r.Error is not null) check.Error = $"Row {r.Row}: {r.Error}";
                else if (r.Quarters.Concat([r.Total]).Any(v => v < 0 || v != Math.Floor(v))) check.Error = $"Row {r.Row}: quantities must be whole numbers, 0 or more.";
                if (hasQuarters)
                {
                    check.Q1 = (int)r.Quarters[0]; check.Q2 = (int)r.Quarters[1]; check.Q3 = (int)r.Quarters[2]; check.Q4 = (int)r.Quarters[3];
                    if (r.Total > 0 && Math.Abs(r.Total - total) > 0.001)
                        check.Warning = $"Total for the year says {r.Total:0}, but Q1–Q4 add up to {total:0}. Q1–Q4 are used.";
                    else if (Enumerable.Range(0, 4).Any(q => r.Months.Skip(q * 3).Take(3).Any(m => m > 0) && Math.Abs(r.Months.Skip(q * 3).Take(3).Sum() - r.Quarters[q]) > 0.001))
                        check.Warning = "The monthly figures don't add up to the quarters. The quarters are used.";
                }
                check.Total = (int)total;
                sheetTotals[key] = sheetTotals.GetValueOrDefault(key) + total;

                // Which catalog item?
                SupplyItem? item = r.Code is not null ? byCode.GetValueOrDefault(r.Code.ToUpperInvariant()) : byName.GetValueOrDefault(NormalName(r.Name));
                if (item is not null)
                {
                    check.ItemId = item.ItemId;
                    check.StockNo = item.StockNo;
                    current.TryGetValue((office.OfficeId, item.ItemId), out var existing);
                    check.CurrentTotal = existing?.AllocatedQty;
                    check.AlreadyUsed = (existing?.IssuedQty ?? 0) + pending.GetValueOrDefault((office.OfficeId, item.ItemId));
                    if (check.Error is null && check.Total < check.AlreadyUsed)
                        check.Error = $"Row {r.Row}: {office.OfficeAcronym} has already used {check.AlreadyUsed} (issued or pending). The new total can't be lower.";
                    if (check.Error is null && check.Total == 0 && check.CurrentTotal is null) continue;   // not needed, never was
                }
                else
                {
                    if (check.Error is null && check.Total == 0) continue;                                // not needed, not in catalog
                    if (!newItems.TryGetValue(key, out var n))
                    {
                        n = new NewCatalogItem
                        {
                            Key = key, Name = r.Name, Unit = r.Unit.Length > 0 ? r.Unit : "piece", Price = (decimal)Math.Round(r.Price, 2),
                            Category = r.Section.Length > 0 ? r.Section : "Other Items", FromPartII = r.Part == 2,
                            StockNo = r.Code ?? $"OTH-{nextOther++:000}",
                        };
                        newItems[key] = n;
                    }
                    else if (n.Price == 0 && r.Price > 0) n.Price = (decimal)Math.Round(r.Price, 2);
                    check.NewItemKey = key;
                    check.StockNo = n.StockNo;
                }

                info.Items += check.Total > 0 ? 1 : 0;
                info.Units += check.Total;
                info.Warnings += check.Warning is not null ? 1 : 0;
                if (check.Error is not null) info.Status = "Error";
                preview.Rows.Add(check);
            }
        }

        preview.NewItems = newItems.Values.OrderBy(n => n.FromPartII).ThenBy(n => n.Category).ThenBy(n => n.Name).ToList();

        // Compare with the consolidated sheet (information only)
        if (consolidated is not null)
        {
            var diffs = consolidated
                .Select(r => new { r.Name, Key = r.Code is not null ? r.Code.ToUpperInvariant() : "P2:" + NormalName(r.Name), Total = r.Quarters.Any(q => q > 0) ? r.Quarters.Sum() : r.Total })
                .Where(r => Math.Abs(r.Total - sheetTotals.GetValueOrDefault(r.Key)) > 0.001)
                .ToList();
            if (diffs.Count > 0)
            {
                var ex = diffs[0];
                preview.Notices.Add($"The \"{consolidatedName}\" sheet differs from the office sheets added together on {diffs.Count} item(s), e.g. {ex.Name}: {ex.Total:0} there, {sheetTotals.GetValueOrDefault(ex.Key):0} on the office sheets. Each office's own sheet is what gets imported.");
            }
            else preview.Notices.Add($"The \"{consolidatedName}\" sheet matches the office sheets added together.");
        }
        var skipped = preview.Sheets.Where(x => x.Status == "Skipped").Select(x => x.Sheet).ToList();
        if (skipped.Count > 0) preview.Notices.Add($"Skipped (no quantities): {string.Join(", ", skipped)}.");

        if (!preview.Sheets.Any(x => x.Status is "Read" or "Error"))
            preview.FileError = "No office sheet with quantities was found in this workbook.";
        else if (preview.Rows.Count == 0)
            preview.FileError = "No quantities were found in the office sheets.";
        return preview;
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

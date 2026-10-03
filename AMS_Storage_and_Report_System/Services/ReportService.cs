// ReportService.cs — AMS reports, on screen and as Excel files:
//   • RSMI (Report of Supplies and Materials Issued) for a month
//   • APP-CSE utilization per office
//   • Reorder list (items at or below their low-stock level)
//   • Stock card per item (rows come from StockService)

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed record RsmiLine(DateTime IssuedAt, string RisNo, string? ResponsibilityCenterCode, string OfficeAcronym,
                              string StockNo, string Item, string Unit, int Quantity, decimal UnitCost)
{
    public decimal Amount => Quantity * UnitCost;
}

public sealed record RsmiRecap(string StockNo, string Item, string Unit, int Quantity, decimal UnitCost, decimal TotalCost);

public sealed record UtilizationRow(string OfficeAcronym, string OfficeName, string StockNo, string Item, string Unit,
                                   int Allocated, int Issued, int Pending)
{
    public int Remaining => Math.Max(0, Allocated - Issued - Pending);
    public double UsedPercent => Allocated == 0 ? 0 : Math.Round(100.0 * (Issued + Pending) / Allocated, 1);
}

public sealed record ReorderRow(string StockNo, string Item, string Unit, int OnHand, int ReorderLevel, int IssuedLast90Days, decimal UnitPrice)
{
    /// A rough guide: enough to cover about three months of issues plus the low-stock buffer.
    public int SuggestedOrder => Math.Max(0, IssuedLast90Days + ReorderLevel - OnHand);
}

public sealed class ReportService(IDbContextFactory<AppDbContext> dbFactory, StockService stock)
{
    // ── RSMI ────────────────────────────────────────────────────────────
    public async Task<List<RsmiLine>> RsmiAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RisItems.AsNoTracking()
            .Join(db.RisTransactions.Where(r => r.Status == RisStatus.Issued && r.IssuedAt >= from && r.IssuedAt < to),
                  i => i.RisId, r => r.RisId, (i, r) => new { i, r })
            .Where(x => x.i.IssuedQty > 0)
            .OrderBy(x => x.r.IssuedAt).ThenBy(x => x.r.RisNo).ThenBy(x => x.i.LineNo)
            .Select(x => new RsmiLine(x.r.IssuedAt!.Value, x.r.RisNo, x.r.ResponsibilityCenterCode, x.r.Office!.OfficeAcronym,
                                      x.i.StockNo, x.i.ItemDescription, x.i.UnitOfMeasure, x.i.IssuedQty!.Value, x.i.UnitCost))
            .ToListAsync();
    }

    public static List<RsmiRecap> Recap(IEnumerable<RsmiLine> lines) =>
        lines.GroupBy(l => new { l.StockNo, l.UnitCost })
             .Select(g => new RsmiRecap(g.Key.StockNo, g.First().Item, g.First().Unit, g.Sum(l => l.Quantity), g.Key.UnitCost, g.Sum(l => l.Amount)))
             .OrderBy(r => r.StockNo).ToList();

    // ── APP-CSE utilization ─────────────────────────────────────────────
    public async Task<List<UtilizationRow>> UtilizationAsync(int year, int officeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pending = await db.RisItems
            .Join(db.RisTransactions.Where(r => r.FiscalYear == year && r.Status == RisStatus.Pending), i => i.RisId, r => r.RisId, (i, r) => new { r.OfficeId, i.ItemId, i.RequestedQty })
            .GroupBy(x => new { x.OfficeId, x.ItemId })
            .Select(g => new { g.Key.OfficeId, g.Key.ItemId, Qty = g.Sum(x => x.RequestedQty) })
            .ToDictionaryAsync(x => (x.OfficeId, x.ItemId), x => x.Qty);

        var rows = await db.AppCseAllocations.AsNoTracking()
            .Where(a => a.FiscalYear == year && (officeId == 0 || a.OfficeId == officeId))
            .Select(a => new { a.OfficeId, a.ItemId, a.Office!.OfficeAcronym, a.Office.OfficeName, a.Item!.StockNo, a.Item.ItemName, a.Item.UnitOfMeasure, a.AllocatedQty, a.IssuedQty })
            .OrderBy(a => a.OfficeAcronym).ThenBy(a => a.StockNo)
            .ToListAsync();

        return rows.Select(a => new UtilizationRow(a.OfficeAcronym, a.OfficeName, a.StockNo, a.ItemName, a.UnitOfMeasure,
                                                   a.AllocatedQty, a.IssuedQty, pending.GetValueOrDefault((a.OfficeId, a.ItemId)))).ToList();
    }

    // ── Reorder list ────────────────────────────────────────────────────
    public async Task<List<ReorderRow>> ReorderAsync(bool onlyLow)
    {
        var since = PhTime.Now.AddDays(-90);
        await using var db = await dbFactory.CreateDbContextAsync();
        var issued = await db.StockMovements.Where(m => m.MovementType == "ISSUE" && m.CreatedAt >= since)
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Qty = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
        var items = await db.SupplyItems.AsNoTracking()
            .Where(i => i.IsActive && (!onlyLow || i.StockOnHand <= i.ReorderLevel))
            .OrderBy(i => i.StockOnHand).ThenBy(i => i.ItemName).ToListAsync();
        return items.Select(i => new ReorderRow(i.StockNo, i.ItemName, i.UnitOfMeasure, i.StockOnHand, i.ReorderLevel,
                                                issued.GetValueOrDefault(i.ItemId), i.UnitPrice)).ToList();
    }

    // ── Excel exports ───────────────────────────────────────────────────
    public async Task<byte[]> RsmiExcelAsync(int year, int month, string entityName)
    {
        var lines = await RsmiAsync(year, month);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("RSMI");
        ws.Cell(1, 1).Value = "REPORT OF SUPPLIES AND MATERIALS ISSUED";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = $"Entity Name: {entityName}";
        ws.Cell(3, 1).Value = $"Period: {new DateTime(year, month, 1):MMMM yyyy}";
        var r = Header(ws, 5, "Date issued", "RIS No.", "Responsibility Center Code", "Office", "Stock No.", "Item", "Unit", "Quantity Issued", "Unit Cost", "Amount");
        foreach (var l in lines)
        {
            Row(ws, r++, l.IssuedAt.ToString("yyyy-MM-dd"), l.RisNo, l.ResponsibilityCenterCode ?? "", l.OfficeAcronym, l.StockNo, l.Item, l.Unit, l.Quantity, (double)l.UnitCost, (double)l.Amount);
        }
        Total(ws, r, 10, lines.Sum(l => l.Amount));

        var recap = wb.Worksheets.Add("Recapitulation");
        var rr = Header(recap, 1, "Stock No.", "Item", "Unit", "Quantity", "Unit Cost", "Total Cost", "UACS Object Code");
        var recaps = Recap(lines);
        foreach (var x in recaps) Row(recap, rr++, x.StockNo, x.Item, x.Unit, x.Quantity, (double)x.UnitCost, (double)x.TotalCost, "");
        Total(recap, rr, 6, recaps.Sum(x => x.TotalCost));
        return Save(wb, ws, recap);
    }

    public async Task<byte[]> UtilizationExcelAsync(int year, int officeId)
    {
        var rows = await UtilizationAsync(year, officeId);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add($"APP-CSE {year}");
        var r = Header(ws, 1, "Office", "Stock No.", "Item", "Unit", "APP-CSE", "Issued", "Pending", "Remaining", "% used");
        foreach (var x in rows) Row(ws, r++, x.OfficeAcronym, x.StockNo, x.Item, x.Unit, x.Allocated, x.Issued, x.Pending, x.Remaining, x.UsedPercent);
        return Save(wb, ws);
    }

    public async Task<byte[]> ReorderExcelAsync(bool onlyLow)
    {
        var rows = await ReorderAsync(onlyLow);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Reorder list");
        var r = Header(ws, 1, "Stock No.", "Item", "Unit", "On hand", "Low-stock level", "Issued, last 90 days", "Suggested order", "Unit price", "Estimated cost");
        foreach (var x in rows) Row(ws, r++, x.StockNo, x.Item, x.Unit, x.OnHand, x.ReorderLevel, x.IssuedLast90Days, x.SuggestedOrder, (double)x.UnitPrice, (double)(x.SuggestedOrder * x.UnitPrice));
        return Save(wb, ws);
    }

    public async Task<byte[]?> StockCardExcelAsync(int itemId)
    {
        var (item, rows) = await stock.StockCardAsync(itemId);
        if (item is null) return null;
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Stock Card");
        ws.Cell(1, 1).Value = "STOCK CARD";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = $"Item: {item.ItemName}{(string.IsNullOrWhiteSpace(item.Specifications) ? "" : ", " + item.Specifications)}";
        ws.Cell(3, 1).Value = $"Stock No.: {item.StockNo}    Unit: {item.UnitOfMeasure}    Re-order point: {item.ReorderLevel}";
        var r = Header(ws, 5, "Date", "Reference", "Type", "Receipt Qty.", "Issue Qty.", "Office", "Balance Qty.", "Remarks");
        foreach (var x in rows) Row(ws, r++, x.Date.ToString("yyyy-MM-dd"), x.Reference ?? "", x.Type, x.Receipt == 0 ? Blank.Value : (XLCellValue)x.Receipt, x.Issue == 0 ? Blank.Value : (XLCellValue)x.Issue, x.Office ?? "", x.Balance, x.Remarks ?? "");
        return Save(wb, ws);
    }

    // ── Excel helpers ───────────────────────────────────────────────────
    private static int Header(IXLWorksheet ws, int row, params string[] titles)
    {
        for (var c = 0; c < titles.Length; c++) ws.Cell(row, c + 1).Value = titles[c];
        var range = ws.Range(row, 1, row, titles.Length);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#00007F");
        ws.SheetView.FreezeRows(row);
        return row + 1;
    }

    private static void Row(IXLWorksheet ws, int row, params XLCellValue[] values)
    {
        for (var c = 0; c < values.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = values[c];
            if (values[c].IsNumber && values[c].GetNumber() % 1 != 0) cell.Style.NumberFormat.Format = "#,##0.00";
        }
    }

    private static void Total(IXLWorksheet ws, int row, int column, decimal amount)
    {
        ws.Cell(row, column - 1).Value = "Total";
        ws.Cell(row, column).Value = (double)amount;
        ws.Cell(row, column).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(row, column - 1, row, column).Style.Font.Bold = true;
    }

    private static byte[] Save(XLWorkbook wb, params IXLWorksheet[] sheets)
    {
        foreach (var ws in sheets) ws.Columns().AdjustToContents(1, 400, 8, 60);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

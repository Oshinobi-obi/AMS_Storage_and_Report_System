// SupplyDashboardService.cs — office-supply figures for the dashboard and its Excel report:
//   • stock level of every item (on hand, reserved for pickup, low-stock level)
//   • the most requested supplies for a period
//   • requisitions by status
// The Excel report has the same figures as tables, each with a real Excel chart.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public enum StockLevel { Out, Low, Ok }

public sealed record StockLevelRow(int ItemId, string StockNo, string Item, string Category, string Unit,
                                   int OnHand, int Reserved, int ReorderLevel, decimal UnitPrice)
{
    public int Available => Math.Max(OnHand - Reserved, 0);
    public StockLevel Level => OnHand <= 0 ? StockLevel.Out : OnHand <= ReorderLevel ? StockLevel.Low : StockLevel.Ok;
    public string LevelLabel => Level switch { StockLevel.Out => "Out of stock", StockLevel.Low => "Low stock", _ => "In stock" };
    /// On hand compared with the low-stock level (1.0 = exactly at the level). Used to sort "closest to running out".
    public double Cover => ReorderLevel <= 0 ? (OnHand <= 0 ? 0 : double.MaxValue) : (double)OnHand / ReorderLevel;
    public decimal Value => OnHand * UnitPrice;
}

public sealed record RequestedRow(int ItemId, string StockNo, string Item, string Unit,
                                  int Requests, int Offices, int QtyRequested, int QtyIssued);

public sealed record StatusCount(string Label, int Count, string Color);

public sealed class SupplyDashboardService(IDbContextFactory<AppDbContext> dbFactory, RequisitionAdminService requisitions)
{
    // Status colours: the same green / amber / red as the badges in Inventory.
    public const string OkColor = "#1E9E5A", LowColor = "#E0A400", OutColor = "#D63A3A", Brand = "#0B2FA8";

    /// "all", "year", "quarter" or "month" → the start date (null = all time) and a label for reports.
    public static (DateTime? Since, string Label) Period(string? key)
    {
        var now = PhTime.Now;
        return key switch
        {
            "year" => (new DateTime(now.Year, 1, 1), $"This year ({now.Year})"),
            "quarter" => (new DateTime(now.Year, (now.Month - 1) / 3 * 3 + 1, 1), $"This quarter (Q{(now.Month - 1) / 3 + 1} {now.Year})"),
            "month" => (new DateTime(now.Year, now.Month, 1), $"This month ({now:MMMM yyyy})"),
            _ => (null, "All time"),
        };
    }

    public async Task<List<StockLevelRow>> StockLevelsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var reserved = await requisitions.ReservedByItemAsync();
        var items = await db.SupplyItems.AsNoTracking().Where(i => i.IsActive)
            .Select(i => new { i.ItemId, i.StockNo, i.ItemName, Category = i.Category != null ? i.Category.CategoryName : "Uncategorized",
                               i.UnitOfMeasure, i.StockOnHand, i.ReorderLevel, i.UnitPrice })
            .ToListAsync();
        return items
            .Select(i => new StockLevelRow(i.ItemId, i.StockNo, i.ItemName, i.Category, i.UnitOfMeasure,
                                           i.StockOnHand, reserved.GetValueOrDefault(i.ItemId), i.ReorderLevel, i.UnitPrice))
            .OrderBy(r => r.Level).ThenBy(r => r.Cover).ThenBy(r => r.Item)
            .ToList();
    }

    /// Ranked by how many requisitions asked for the item (cancelled ones don't count),
    /// then by quantity. Counting requisitions keeps reams, boxes and pieces comparable.
    public async Task<List<RequestedRow>> MostRequestedAsync(DateTime? since, int take = 10)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.RisItems.AsNoTracking()
            .Join(db.RisTransactions.Where(r => r.Status != RisStatus.Cancelled && (since == null || r.RequestedAt >= since)),
                  i => i.RisId, r => r.RisId, (i, r) => new { i.ItemId, i.RisId, r.OfficeId, i.RequestedQty, Issued = r.Status == RisStatus.Issued ? i.IssuedQty ?? 0 : 0 })
            .GroupBy(x => x.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Requests = g.Select(x => x.RisId).Distinct().Count(),
                Offices = g.Select(x => x.OfficeId).Distinct().Count(),
                Qty = g.Sum(x => x.RequestedQty),
                Issued = g.Sum(x => x.Issued),
            })
            .OrderByDescending(x => x.Requests).ThenByDescending(x => x.Qty)
            .Take(take)
            .ToListAsync();

        var ids = rows.Select(r => r.ItemId).ToList();
        var items = await db.SupplyItems.AsNoTracking().Where(i => ids.Contains(i.ItemId))
            .ToDictionaryAsync(i => i.ItemId, i => new { i.StockNo, i.ItemName, i.UnitOfMeasure });
        return rows.Select(r =>
        {
            var i = items.GetValueOrDefault(r.ItemId);
            return new RequestedRow(r.ItemId, i?.StockNo ?? "", i?.ItemName ?? $"Item {r.ItemId}", i?.UnitOfMeasure ?? "",
                                    r.Requests, r.Offices, r.Qty, r.Issued);
        }).ToList();
    }

    public async Task<List<StatusCount>> RequisitionsByStatusAsync(DateTime? since)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var counts = await db.RisTransactions.AsNoTracking()
            .Where(r => since == null || r.RequestedAt >= since)
            .GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return
        [
            new("Pending approval", counts.GetValueOrDefault(RisStatus.Pending), "#E0A400"),
            new("For issuance", counts.GetValueOrDefault(RisStatus.Approved), "#2F6FDE"),
            new("Issued", counts.GetValueOrDefault(RisStatus.Issued), "#1E9E5A"),
            new("Rejected", counts.GetValueOrDefault(RisStatus.Rejected), "#D63A3A"),
            new("Cancelled", counts.GetValueOrDefault(RisStatus.Cancelled), "#6F7A90"),
        ];
    }

    // ── Excel report ────────────────────────────────────────────────────
    public async Task<byte[]> ExcelAsync(DateTime? since, string periodLabel, string entityName)
    {
        var stock = await StockLevelsAsync();
        var top = await MostRequestedAsync(since, 15);
        var status = await RequisitionsByStatusAsync(since);
        var charts = new List<ExcelChart>();
        var navy = XLColor.FromHtml("#00007F");

        using var wb = new XLWorkbook();

        // ── Summary ──
        var sum = wb.Worksheets.Add("Summary");
        sum.Cell(1, 1).Value = "OFFICE SUPPLIES REPORT";
        sum.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(navy);
        sum.Cell(2, 1).Value = string.IsNullOrWhiteSpace(entityName) ? "Asset Management Section" : $"{entityName} · Asset Management Section";
        sum.Cell(3, 1).Value = $"Requisitions for: {periodLabel}    Stock levels as of {PhTime.Now:MMMM d, yyyy h:mm tt}";
        sum.Range(2, 1, 3, 1).Style.Font.SetFontColor(XLColor.FromHtml("#565F7A"));

        var counts = new[]
        {
            ("In stock", stock.Count(s => s.Level == StockLevel.Ok), OkColor),
            ("Low stock", stock.Count(s => s.Level == StockLevel.Low), LowColor),
            ("Out of stock", stock.Count(s => s.Level == StockLevel.Out), OutColor),
        };
        var r = Header(sum, 5, "Stock level", "Items");
        var firstCount = r;
        foreach (var (label, n, _) in counts) { sum.Cell(r, 1).Value = label; sum.Cell(r, 2).Value = n; r++; }
        sum.Cell(r, 1).Value = "Total items"; sum.Cell(r, 2).FormulaA1 = $"SUM(B{firstCount}:B{r - 1})";
        sum.Range(r, 1, r, 2).Style.Font.Bold = true;
        r++;
        sum.Cell(r, 1).Value = "Value on hand (₱)"; sum.Cell(r, 2).Value = (double)stock.Sum(s => s.Value);
        sum.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
        sum.Range(r, 1, r, 2).Style.Font.Bold = true;
        charts.Add(new ExcelChart("Summary", ExcelChartKind.Pie, "Stock level of office supplies",
            ExcelCharts.Range("Summary", 1, firstCount, firstCount + 2), counts.Select(c => c.Item1).ToList(),
            [new ExcelSeries("Items", ExcelCharts.Range("Summary", 2, firstCount, firstCount + 2), counts.Select(c => (double)c.Item2).ToList(), OkColor)],
            FromCol: 3, FromRow: 4, ToCol: 10, ToRow: 20, PointColors: counts.Select(c => c.Item3).ToList()));

        r = 23;
        var rr = Header(sum, r, "Requisitions", "Count");
        var firstStatus = rr;
        foreach (var s in status) { sum.Cell(rr, 1).Value = s.Label; sum.Cell(rr, 2).Value = s.Count; rr++; }
        sum.Cell(rr, 1).Value = "Total"; sum.Cell(rr, 2).FormulaA1 = $"SUM(B{firstStatus}:B{rr - 1})";
        sum.Range(rr, 1, rr, 2).Style.Font.Bold = true;
        charts.Add(new ExcelChart("Summary", ExcelChartKind.Column, $"Requisitions by status ({periodLabel})",
            ExcelCharts.Range("Summary", 1, firstStatus, firstStatus + status.Count - 1), status.Select(s => s.Label).ToList(),
            [new ExcelSeries("Requisitions", ExcelCharts.Range("Summary", 2, firstStatus, firstStatus + status.Count - 1), status.Select(s => (double)s.Count).ToList(), Brand)],
            FromCol: 3, FromRow: 22, ToCol: 10, ToRow: 38, PointColors: status.Select(s => s.Color).ToList()));
        sum.Column(1).Width = 22; sum.Column(2).Width = 14;

        // ── Stock levels ──
        var st = wb.Worksheets.Add("Stock levels");
        var sr = Header(st, 1, "Stock No.", "Item", "Category", "Unit", "On hand", "Reserved for pickup", "Available", "Low-stock level", "Status", "Unit price", "Value on hand");
        var firstStock = sr;
        foreach (var s in stock)
        {
            Row(st, sr, s.StockNo, s.Item, s.Category, s.Unit, s.OnHand, s.Reserved, s.Available, s.ReorderLevel, s.LevelLabel, (double)s.UnitPrice, (double)s.Value);
            var statusCell = st.Cell(sr, 9);
            statusCell.Style.Font.Bold = true;
            statusCell.Style.Font.FontColor = XLColor.FromHtml(s.Level switch { StockLevel.Out => OutColor, StockLevel.Low => "#9A6B00", _ => OkColor });
            st.Cell(sr, 10).Style.NumberFormat.Format = "#,##0.00";
            st.Cell(sr, 11).Style.NumberFormat.Format = "#,##0.00";
            sr++;
        }
        st.Columns(1, 11).AdjustToContents(1, Math.Max(sr, 2), 8, 50);
        // Chart: the 15 items closest to running out (the sheet is sorted that way)
        var watch = Math.Min(15, stock.Count);
        if (watch > 0)
        {
            var last = firstStock + watch - 1;
            var w = stock.Take(watch).ToList();
            charts.Add(new ExcelChart("Stock levels", ExcelChartKind.Bar, "Closest to running out: on hand vs. low-stock level",
                ExcelCharts.Range("Stock levels", 2, firstStock, last), w.Select(s => s.Item).ToList(),
                [
                    new ExcelSeries("On hand", ExcelCharts.Range("Stock levels", 5, firstStock, last), w.Select(s => (double)s.OnHand).ToList(), Brand),
                    new ExcelSeries("Low-stock level", ExcelCharts.Range("Stock levels", 8, firstStock, last), w.Select(s => (double)s.ReorderLevel).ToList(), "#E0A400"),
                ],
                FromCol: 12, FromRow: 1, ToCol: 22, ToRow: 1 + Math.Max(18, watch * 2 + 4)));
        }
        if (sr > firstStock) st.Range(firstStock - 1, 1, sr - 1, 11).SetAutoFilter();

        // ── Most requested ──
        var mr = wb.Worksheets.Add("Most requested");
        mr.Cell(1, 1).Value = $"Most requested office supplies, {periodLabel}";
        mr.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(13).Font.SetFontColor(navy);
        mr.Cell(2, 1).Value = "Ranked by the number of requisitions that asked for the item. Cancelled requisitions are not counted.";
        mr.Cell(2, 1).Style.Font.SetFontColor(XLColor.FromHtml("#565F7A")).Font.SetItalic();
        var tr = Header(mr, 4, "Rank", "Stock No.", "Item", "Unit", "Requisitions", "Offices", "Quantity requested", "Quantity issued");
        var firstTop = tr;
        for (var i = 0; i < top.Count; i++)
        {
            var t = top[i];
            Row(mr, tr++, i + 1, t.StockNo, t.Item, t.Unit, t.Requests, t.Offices, t.QtyRequested, t.QtyIssued);
        }
        if (top.Count == 0) mr.Cell(tr, 1).Value = "No requisitions in this period.";
        mr.Columns(1, 8).AdjustToContents(4, Math.Max(tr, 5), 6, 50);
        if (top.Count > 0)
        {
            var last = firstTop + top.Count - 1;
            charts.Add(new ExcelChart("Most requested", ExcelChartKind.Bar, $"Most requested supplies ({periodLabel})",
                ExcelCharts.Range("Most requested", 3, firstTop, last), top.Select(t => t.Item).ToList(),
                [new ExcelSeries("Requisitions", ExcelCharts.Range("Most requested", 5, firstTop, last), top.Select(t => (double)t.Requests).ToList(), Brand)],
                FromCol: 9, FromRow: 3, ToCol: 18, ToRow: 3 + Math.Max(18, top.Count * 2 + 4)));
        }

        foreach (var ws in wb.Worksheets) { ws.PageSetup.PageOrientation = XLPageOrientation.Landscape; ws.PageSetup.FitToPages(1, 0); }
        sum.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sum.PageSetup.FitToPages(1, 1);
        sum.SetTabActive();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ExcelCharts.Add(ms.ToArray(), charts);
    }

    private static int Header(IXLWorksheet ws, int row, params string[] titles)
    {
        for (var c = 0; c < titles.Length; c++) ws.Cell(row, c + 1).Value = titles[c];
        var range = ws.Range(row, 1, row, titles.Length);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#00007F");
        return row + 1;
    }

    private static void Row(IXLWorksheet ws, int row, params XLCellValue[] values)
    {
        for (var c = 0; c < values.Length; c++) ws.Cell(row, c + 1).Value = values[c];
    }
}

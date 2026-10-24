// Tables show 10 rows at a time, with <Pager> under them.
//   @foreach (var r in Visible.Page(_page)) { ... }
//   <Pager Total="Visible.Count()" @bind-Page="_page" />
// Printed tables (Reports, Stock card) keep every row in the page and just hide the others on
// screen with Paging.RowClass, so the printout still has every row.

namespace AMS_Storage_and_Report_System.Services;

public static class Paging
{
    public const int Size = 10;

    /// The rows of one page (the page number is kept within the range).
    public static IEnumerable<T> Page<T>(this IEnumerable<T> items, int page, int size = Size)
    {
        var list = items as IReadOnlyCollection<T> ?? items.ToList();
        var pages = Math.Max(1, (list.Count + size - 1) / size);
        var p = Math.Clamp(page, 1, pages);
        return list.Skip((p - 1) * size).Take(size);
    }

    /// For printed tables: "" for rows on the current page, "pg-off" (hidden on screen but printed) for the rest.
    public static string RowClass(int index, int page, int total, int size = Size)
    {
        var pages = Math.Max(1, (total + size - 1) / size);
        var p = Math.Clamp(page, 1, pages);
        return index / size == p - 1 ? "" : "pg-off";
    }
}

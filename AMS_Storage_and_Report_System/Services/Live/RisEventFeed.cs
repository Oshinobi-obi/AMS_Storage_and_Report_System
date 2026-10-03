// RisEventFeed.cs — watches the database for requisition status changes made by
// EITHER website (AMS Supplies portal or AMS StockWatch) and tells every open page.
// One query every few seconds for the whole server, not one per user.

using AMS_Storage_and_Report_System.Data;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services.Live;

public sealed class RisEvent
{
    public long HistoryId { get; set; }
    public long RisId { get; set; }
    public long OfficeId { get; set; }
    public string RisNo { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string OfficeAcronym { get; set; } = "";
}

public sealed class RisEventFeed(IDbContextFactory<AppDbContext> dbFactory, ILogger<RisEventFeed> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private long _lastId = -1;

    /// Raised on a background thread; components must use InvokeAsync.
    public event Action<IReadOnlyList<RisEvent>>? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex.GetBaseException().Message.Contains("doesn't exist"))
            {
                // Supply tables not created yet; keep waiting quietly.
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Live update check failed; will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (_lastId < 0)
        {
            // Start from "now": only changes made after the site started are announced.
            _lastId = await db.Database
                .SqlQuery<long>($"SELECT CAST(COALESCE(MAX(history_id), 0) AS SIGNED) AS Value FROM ris_status_history")
                .SingleAsync(ct);
            return;
        }

        var events = await db.Database.SqlQuery<RisEvent>($@"
            SELECT CAST(h.history_id AS SIGNED) AS HistoryId, CAST(h.ris_id AS SIGNED) AS RisId,
                   CAST(r.office_id AS SIGNED) AS OfficeId, r.ris_no AS RisNo,
                   h.to_status AS ToStatus, o.office_acronym AS OfficeAcronym
              FROM ris_status_history h
              JOIN ris_transactions r ON r.ris_id = h.ris_id
              JOIN offices o ON o.office_id = r.office_id
             WHERE h.history_id > {_lastId}
             ORDER BY h.history_id
             LIMIT 100").ToListAsync(ct);

        if (events.Count == 0) return;
        _lastId = events[^1].HistoryId;
        Changed?.Invoke(events);
    }
}

/// Per browser tab: pages subscribe here to reload their data silently
/// when something relevant changes.
public sealed class LiveRefresh
{
    public event Func<IReadOnlyList<RisEvent>, Task>? Changed;

    public async Task RaiseAsync(IReadOnlyList<RisEvent> events)
    {
        if (Changed is null) return;
        foreach (var handler in Changed.GetInvocationList().Cast<Func<IReadOnlyList<RisEvent>, Task>>())
        {
            try { await handler(events); } catch { /* one page failing must not stop the others */ }
        }
    }
}

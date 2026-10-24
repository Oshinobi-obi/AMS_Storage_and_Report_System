// This is the live update watcher. It checks the database for requisition status changes
// made on EITHER website (e-SRS or e-PINS) and tells every open page.
// It's one query every few seconds for the whole server, not one per user.

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

    /// This runs on a background thread, so components have to use InvokeAsync.
    public event Action<IReadOnlyList<RisEvent>>? Changed;

    /// Runs when other shared data changes (like "announcements", "catalog", "appcse").
    public event Action<IReadOnlySet<string>>? AreasChanged;

    private readonly Dictionary<string, string> _signatures = new();

    // Status for /live/status: is the watcher running, and what did it last see?
    public DateTime? LastCheckUtc { get; private set; }
    public DateTime? LastChangeUtc { get; private set; }
    public string? LastChangedAreas { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastErrorUtc { get; private set; }
    public IReadOnlyDictionary<string, string> Signatures => _signatures;
    private DateTime _lastLoggedError = DateTime.MinValue;

    private void NoteError(string where, Exception ex)
    {
        LastError = $"{where}: {ex.GetBaseException().Message}";
        LastErrorUtc = DateTime.UtcNow;
        if (DateTime.UtcNow - _lastLoggedError > TimeSpan.FromMinutes(5))   // log it, but not every 5 seconds
        {
            _lastLoggedError = DateTime.UtcNow;
            log.LogWarning(ex, "Live updates: {Where} check failed", where);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            // Nobody has the website open, so I skip the database checks and the shared database
            // isn't being queried all day and night. When a page opens again, I start fresh
            // (old changes aren't announced again).
            if (Changed is null && AreasChanged is null)
            {
                _lastId = -1;
                _signatures.Clear();
                continue;
            }

            try { await PollAreasAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { NoteError("areas", ex); }

            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex.GetBaseException().Message.Contains("doesn't exist"))
            {
                // The supply tables aren't made yet, so I just keep waiting quietly.
            }
            catch (Exception ex)
            {
                NoteError("requisitions", ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (_lastId < 0)
        {
            // Start from "now", so only changes made after the site started are announced.
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

    private async Task PollAreasAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = new HashSet<string>();
        foreach (var (area, sql) in AreaQueries(AMS_Storage_and_Report_System.Services.PhTime.Now))
        {
            string? sig;
            try { sig = await db.Database.SqlQuery<string>(sql).FirstOrDefaultAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing table only means that database update wasn't run yet. Anything else gets logged.
                if (!ex.GetBaseException().Message.Contains("doesn't exist")) NoteError(area, ex);
                continue;
            }
            if (sig is null) continue;
            if (_signatures.TryGetValue(area, out var old) && old != sig) changed.Add(area);
            _signatures[area] = sig;
        }
        LastCheckUtc = DateTime.UtcNow;
        if (changed.Count > 0)
        {
            LastChangeUtc = DateTime.UtcNow;
            LastChangedAreas = string.Join(", ", changed);
            AreasChanged?.Invoke(changed);
        }
    }

    // One fingerprint query per area. I pass `now` so a scheduled announcement counts as a change
    // right when it starts or ends, even if nobody edited it.
    private static IEnumerable<(string Area, FormattableString Sql)> AreaQueries(DateTime now)
    {
        // CAST(... AS CHAR) because some MySQL servers return CONCAT() of numbers as raw bytes,
        // and C# can't read that as text ("Unable to cast System.Byte[] to System.String").
        yield return ("announcements", $@"SELECT CAST(CONCAT(
            (SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', announcement_id, title, message, level))), 0))
               FROM announcements WHERE is_active = 1 AND starts_at <= {now} AND (ends_at IS NULL OR ends_at >= {now})), '/',
            (SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', announcement_id, title, message, level, starts_at, IFNULL(ends_at, ''), is_active))), 0))
               FROM announcements)) AS CHAR CHARACTER SET utf8mb4) AS Value");
        yield return ("catalog", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(SUM(stock_on_hand), 0), ':',
            COALESCE(SUM(CRC32(CONCAT_WS('|', item_id, item_name, IFNULL(specifications, ''), unit_price, reorder_level, is_active, IFNULL(image_path, ''), IFNULL(category_id, 0), is_perishable, IFNULL(manufactured_on, ''), IFNULL(expires_on, '')))), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value
            FROM supply_items");
        yield return ("appcse", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(SUM(allocated_qty), 0), ':', COALESCE(SUM(issued_qty), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value FROM app_cse_allocations");
        yield return ("suggestions", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', suggestion_id, status, IFNULL(admin_response, '')))), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value FROM supply_suggestions");
        yield return ("season", $@"SELECT CAST(IFNULL((SELECT setting_value FROM system_settings WHERE setting_key = 'ui.season'), '') AS CHAR CHARACTER SET utf8mb4) AS Value");
        yield return ("property", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', property_id, property_no, description, status, quantity, unit_cost, IFNULL(office_id, 0), IFNULL(personnel_id, 0)))), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value FROM property_items");
        yield return ("documents", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(MAX(document_id), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value FROM property_documents");
        yield return ("personnel", $@"SELECT CAST(CONCAT(COUNT(*), ':', COALESCE(SUM(CRC32(CONCAT_WS('|', personnel_id, office_id, full_name, position))), 0)) AS CHAR CHARACTER SET utf8mb4) AS Value FROM ro_personnel");
    }
}

// Other shared data
// Each area has a small fingerprint (row count and checksum). If the fingerprint changes,
// something in that area was added, edited or deleted, so the open pages reload it.
// This is per browser tab. Pages subscribe here to quietly reload their data
// when something they show changes.
public sealed class LiveRefresh
{
    public event Func<IReadOnlyList<RisEvent>, Task>? Changed;

    /// Other shared data changed. The set has the names of the areas (see RisEventFeed.AreaQueries).
    public event Func<IReadOnlySet<string>, Task>? AreasChanged;

    public async Task RaiseAreasAsync(IReadOnlySet<string> areas)
    {
        if (AreasChanged is null) return;
        foreach (var handler in AreasChanged.GetInvocationList().Cast<Func<IReadOnlySet<string>, Task>>())
        {
            try { await handler(areas); } catch { /* if one page fails it shouldn't stop the others */ }
        }
    }

    public async Task RaiseAsync(IReadOnlyList<RisEvent> events)
    {
        if (Changed is null) return;
        foreach (var handler in Changed.GetInvocationList().Cast<Func<IReadOnlyList<RisEvent>, Task>>())
        {
            try { await handler(events); } catch { /* if one page fails it shouldn't stop the others */ }
        }
    }
}

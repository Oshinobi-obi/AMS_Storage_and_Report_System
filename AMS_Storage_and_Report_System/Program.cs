using System.IO.Compression;
using System.Security.Claims;
using AMS_Storage_and_Report_System.Components;
using AMS_Storage_and_Report_System.Components.Shared.Modals;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using AMS_Storage_and_Report_System.Services;
using AMS_Storage_and_Report_System.Services.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string (User Secrets in development, never in appsettings.json)
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:Default is empty. Run in the project folder: " +
        "dotnet user-secrets set \"ConnectionStrings:Default\" \"server=localhost;port=3306;database=ams_stockwatch;user=...;password=...;\"");

// Data Protection keys (for cookies and antiforgery) saved in MySQL
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("AMS_StockWatch")
    .AddKeyManagementOptions(o => o.XmlRepository = new MySqlXmlRepository(connectionString));
// I encrypt the saved keys with Windows DPAPI on my own PC or server. Shared hosts usually
// don't allow it, so it can be turned off with "DataProtection:UseDpapi": false.
if (OperatingSystem.IsWindows() && builder.Configuration.GetValue("DataProtection:UseDpapi", true))
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("DetailedErrors"))
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 50 * 1024 * 1024);   // PDF uploads

// One short-lived DbContext per operation (Blazor Server circuits stay open a long time).
var serverVersion = ServerVersion.Parse("8.0.46-mysql");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseMySql(connectionString, serverVersion));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ModalService>();
builder.Services.AddScoped<RequisitionAdminService>();
builder.Services.AddScoped<CatalogAdminService>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<AppCseService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SupplyDashboardService>();
builder.Services.AddScoped<SemiExpendableService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddSingleton<SeasonState>();
builder.Services.AddScoped<PropertyService>();
builder.Services.AddScoped<PropertyDocumentLinks>();
builder.Services.AddScoped<PersonnelBirthdays>();
builder.Services.AddScoped<AdminExtrasService>();
// Live updates: one database watcher for the server, one relay per browser tab
builder.Services.AddSingleton<AMS_Storage_and_Report_System.Services.Live.RisEventFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AMS_Storage_and_Report_System.Services.Live.RisEventFeed>());
builder.Services.AddScoped<AMS_Storage_and_Report_System.Services.Live.LiveRefresh>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/dashboard";
        options.Cookie.Name = "ams_stockwatch_auth";
        options.Cookie.HttpOnly = true;
        // Secure-only cookies need HTTPS. "Auth:AllowHttpCookies": true is only TEMPORARY for a
        // server that has no SSL certificate yet. Set it back to false once the site has HTTPS.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Auth:AllowHttpCookies", false)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;   // Strict drops the cookie when the site is opened from a link
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // Check the account again every 5 minutes: deactivated, reset or changed password means logged out
        options.Events.OnValidatePrincipal = SessionGuard.ValidatePrincipalAsync;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
// Open pages also check the account every 5 minutes
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

// Login rate limit per computer (IP address)
// 40 tries per 5 minutes is enough for a whole office on one internet connection,
// but way too few for a password guessing bot. Each account also locks after 5 wrong tries.
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("login", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 40,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.HttpContext.Response.Headers.Location = "/login?error=" + Uri.EscapeDataString("Too many sign-in attempts from this computer. Wait 5 minutes, then try again.");
        return ValueTask.CompletedTask;
    };
});


builder.Services.AddSingleton<PropertyDocumentStorage>();
builder.Services.AddSingleton<DeliveryDocumentStorage>();
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddSingleton<ProfilePictureStorage>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Configuration.GetValue("Auth:AllowHttpCookies", false))
    app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// The pages show personal data, so the browser shouldn't cache them.
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

// Security headers on every response
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";                         // browsers shouldn't guess file types
    h["X-Frame-Options"] = "SAMEORIGIN";                             // other websites can't show this site in a frame
    h["Content-Security-Policy"] = "frame-ancestors 'self'";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();
// Live update status (open /live/status while logged in)
// Shows if the 5-second watcher is running and the last error, if there is one.
app.MapGet("/live/status", (AMS_Storage_and_Report_System.Services.Live.RisEventFeed feed) =>
{
    static string? Ph(DateTime? utc) => utc is { } u ? AMS_Storage_and_Report_System.Services.PhTime.FromUtc(u).ToString("yyyy-MM-dd hh:mm:ss tt") : null;
    var running = feed.LastCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromSeconds(30);
    return Results.Json(new
    {
        running,
        meaning = running ? "Live updates are working." : "The watcher has not checked in the last 30 seconds. Restart the site and see lastError.",
        lastCheck = Ph(feed.LastCheckUtc),
        lastChange = Ph(feed.LastChangeUtc),
        lastChangedAreas = feed.LastChangedAreas,
        lastError = feed.LastError,
        lastErrorAt = Ph(feed.LastErrorUtc),
        watching = feed.Signatures.Keys.OrderBy(k => k).ToArray(),
    });
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Log in / out
static string SafeLocal(string? url) =>
    !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/dashboard";

static List<Claim> BuildClaims(User user) =>
[
    new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
    new(ClaimTypes.Name, user.Username),
    new(ClaimTypes.Role, user.Role.ToString()),
    new("FullName", user.FullName),
    new("DisplayName", user.DisplayName ?? ""),
    new("ProfilePicture", user.ProfilePicturePath ?? ""),
    new("MustChangePassword", user.RequirePasswordChange ? "1" : "0"),
    new(SessionGuard.StampClaim, SessionGuard.PasswordStamp(user.PasswordHash)),
];

app.MapPost("/account/login", async (HttpContext http, AuthService authService, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);

    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var rememberMe = form.ContainsKey("rememberMe");

    var result = await authService.ValidateLoginAsync(username, password, http.Connection.RemoteIpAddress?.ToString());
    if (!result.Succeeded || result.User is null)
    {
        var msg = Uri.EscapeDataString(result.ErrorMessage ?? "Sign-in failed.");
        var back = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.LocalRedirect($"/login?error={msg}{back}");
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(BuildClaims(result.User), CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = rememberMe,
        ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(7) : DateTimeOffset.UtcNow.AddHours(8),
    });

    return result.User.RequirePasswordChange
        ? Results.LocalRedirect("/profile?requirePasswordChange=true")
        : Results.LocalRedirect(SafeLocal(returnUrl));
}).RequireRateLimiting("login");

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login?signedOut=true");
});

// Makes a new cookie after the profile, picture or password changes.
app.MapGet("/account/refresh-session", async (HttpContext http, IDbContextFactory<AppDbContext> dbFactory, string? returnUrl) =>
{
    var username = http.User.Identity?.Name;
    if (string.IsNullOrEmpty(username)) return Results.LocalRedirect("/login");

    await using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);
    if (user is null || !user.IsActive || !user.IsStaff)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.LocalRedirect("/login");
    }

    var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var isPersistent = auth.Properties?.IsPersistent ?? false;
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(BuildClaims(user), CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            ExpiresUtc = isPersistent ? DateTimeOffset.UtcNow.AddDays(7) : DateTimeOffset.UtcNow.AddHours(8),
        });

    return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/profile" : SafeLocal(returnUrl));
}).RequireAuthorization();

// Property documents (PDF)
var docStorage = app.Services.GetRequiredService<PropertyDocumentStorage>();
var pictureStorage = app.Services.GetRequiredService<ProfilePictureStorage>();

string? DocPath(PropertyDocument doc)
{
    var name = Path.GetFileName(doc.FilePath);
    var full = Path.Combine(docStorage.RootPath, name);
    return File.Exists(full) ? full : null;
}

app.MapGet("/documents/view/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var doc = await db.PropertyDocuments.FindAsync(id);
    var path = doc is null ? null : DocPath(doc);
    return path is null ? Results.NotFound() : Results.File(File.OpenRead(path), "application/pdf");
}).RequireAuthorization();

app.MapGet("/documents/download/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var doc = await db.PropertyDocuments.FindAsync(id);
    var path = doc is null ? null : DocPath(doc);
    return path is null ? Results.NotFound() : Results.File(path, "application/pdf", doc!.OriginalFileName);
}).RequireAuthorization();

// The public My accountability page of e-SRS reads the PDFs through here, because the two
// sites keep their files separate. I only answer a caller that sends the secret word in
// "Integration:Key" (the same in the appsettings of both sites). With no key set, this returns "not found".
app.MapGet("/integration/property-documents/{id:int}", async (int id, HttpContext http, IConfiguration config, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var key = config["Integration:Key"];
    var sent = http.Request.Headers["X-AMS-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(key)))
        return Results.NotFound();
    await using var db = await dbFactory.CreateDbContextAsync();
    var doc = await db.PropertyDocuments.FindAsync(id);
    var path = doc is null ? null : DocPath(doc);
    return path is null ? Results.NotFound() : Results.File(File.OpenRead(path), "application/pdf");
});

app.MapGet("/documents/download-bulk", async (string? ids, IDbContextFactory<AppDbContext> dbFactory, ILogger<Program> log) =>
{
    var idList = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
        .Where(n => n is not null).Select(n => n!.Value)
        .Distinct().Take(300).ToList();
    if (idList.Count == 0) return Results.BadRequest("No documents selected.");

    await using var db = await dbFactory.CreateDbContextAsync();
    var docs = await db.PropertyDocuments.AsNoTracking().Include(d => d.Personnel)
        .Where(d => idList.Contains(d.DocumentId)).OrderBy(d => d.DocumentId).ToListAsync();
    var found = docs.Select(d => (Doc: d, Path: DocPath(d))).ToList();
    var missing = found.Where(f => f.Path is null).Select(f => f.Doc).ToList();
    if (missing.Count > 0)
        log.LogWarning("Bulk download: {Missing} of {Total} PDFs not found in {Root}", missing.Count, found.Count, docStorage.RootPath);
    if (found.All(f => f.Path is null))
        return Results.Text($"None of the {found.Count} selected PDF files were found on the server (folder: {docStorage.RootPath}). " +
                            "Check Storage:PropertyDocumentsPath in appsettings, or upload the documents again.", "text/plain", statusCode: 404);

    // I build it in a temp file, not in memory, so hundreds of big scans don't make the site run out of memory.
    // PDFs are already compressed, so saving them as they are is faster and just as small.
    var tempPath = Path.Combine(Path.GetTempPath(), $"ams-docs-{Guid.NewGuid():N}.zip");
    var zipFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    try
    {
        using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doc, path) in found.Where(f => f.Path is not null))
            {
                // Two files with the same name would overwrite each other in the zip.
                var entryName = Path.GetFileName(doc.OriginalFileName);
                if (string.IsNullOrWhiteSpace(entryName)) entryName = $"document-{doc.DocumentId}.pdf";
                if (!usedNames.Add(entryName))
                    entryName = $"{Path.GetFileNameWithoutExtension(entryName)} ({doc.DocumentId}){Path.GetExtension(entryName)}";
                var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(path!);
                fileStream.CopyTo(entryStream);
            }
            if (missing.Count > 0)
            {
                var note = archive.CreateEntry("_files-not-found.txt");
                using var w = new StreamWriter(note.Open());
                w.WriteLine($"{missing.Count} of the selected documents have no PDF file on the server:");
                foreach (var d in missing) w.WriteLine($"- {d.DocumentType} {d.OriginalFileName} ({d.Personnel?.FullName})");
            }
        }
        zipFile.Position = 0;
        return Results.File(zipFile, "application/zip", $"property-documents-{PhTime.Now:yyyyMMdd}.zip");
    }
    catch
    {
        await zipFile.DisposeAsync();
        throw;
    }
}).RequireAuthorization();

// Delivery documents: DR, Sales Invoice and IAR scanned into one PDF (Reports > Deliveries)
app.MapGet("/deliveries/{id:int}/document", async (int id, bool? download, IDbContextFactory<AppDbContext> dbFactory, DeliveryDocumentStorage storage) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var d = await db.SupplyDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.DeliveryId == id);
    var path = d is null ? null : storage.PathOf(d);
    if (path is null) return Results.NotFound();
    var name = $"Delivery {d!.ReferenceNo} {d.ReceivedOn:yyyy-MM-dd}.pdf";
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
    return download == true ? Results.File(path, "application/pdf", name) : Results.File(path, "application/pdf");
}).RequireAuthorization();

// Signed RIS (PDF uploaded when issued)
app.MapGet("/requisitions/{id:int}/signed", async (int id, bool? download, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var copy = await db.RisSignedCopies.AsNoTracking().FirstOrDefaultAsync(c => c.RisId == id);
    if (copy is null) return Results.NotFound();
    var risNo = await db.RisTransactions.Where(r => r.RisId == id).Select(r => r.RisNo).FirstOrDefaultAsync() ?? id.ToString();
    return download == true
        ? Results.File(copy.Content, "application/pdf", $"RIS-{risNo}-signed.pdf")
        : Results.File(copy.Content, "application/pdf");
}).RequireAuthorization();

// Catalog photos (admin preview)
app.MapGet("/catalog/image/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var img = await db.ItemImages.AsNoTracking().FirstOrDefaultAsync(i => i.ItemId == id);
    return img is null ? Results.NotFound() : Results.File(img.Content, img.ContentType);
}).RequireAuthorization();

// Excel downloads
const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

app.MapGet("/exports/app-cse-template", async (int year, AppCseService svc) =>
    Results.File(await svc.TemplateAsync(year), Xlsx, $"APP-CSE-{year}.xlsx")).RequireAuthorization();

app.MapGet("/exports/rsmi", async (int year, int month, ReportService reports, SettingsService settings) =>
{
    if (month is < 1 or > 12 || year is < 2000 or > 2100) return Results.BadRequest();
    var entity = (await settings.LoadAsync()).EntityName;
    return Results.File(await reports.RsmiExcelAsync(year, month, entity), Xlsx, $"RSMI-{year}-{month:00}.xlsx");
}).RequireAuthorization();

app.MapGet("/exports/utilization", async (int year, int? office, ReportService reports) =>
    Results.File(await reports.UtilizationExcelAsync(year, office ?? 0), Xlsx, $"APP-CSE-utilization-{year}.xlsx")).RequireAuthorization();

app.MapGet("/exports/supplies-report", async (string? period, SupplyDashboardService supplies, SettingsService settings) =>
{
    var (since, label) = SupplyDashboardService.Period(period);
    var entity = (await settings.LoadAsync()).EntityName;
    return Results.File(await supplies.ExcelAsync(since, label, entity), Xlsx, $"office-supplies-report-{PhTime.Now:yyyyMMdd}.xlsx");
}).RequireAuthorization();

// SPLV / SPHV deliveries: one delivery, or the filtered list, as Excel or PDF
static string PreparedBy(HttpContext http) =>
    http.User.FindFirst("FullName")?.Value is { Length: > 0 } n ? n : http.User.Identity?.Name ?? "";

app.MapGet("/exports/semi-expendable/{id:int}", async (int id, string? format, HttpContext http, SemiExpendableService svc, SettingsService settings) =>
{
    var entity = (await settings.LoadAsync()).EntityName;
    if (format == "pdf")
        return await svc.DeliveryPdfAsync(id, entity, PreparedBy(http)) is { } pdf
            ? Results.File(pdf, "application/pdf", $"semi-expendable-delivery-{id}.pdf") : Results.NotFound();
    return await svc.DeliveryExcelAsync(id, entity) is { } xlsx
        ? Results.File(xlsx, Xlsx, $"semi-expendable-delivery-{id}.xlsx") : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/exports/semi-expendable", async (int? year, string? type, string? q, string? format, HttpContext http, SemiExpendableService svc, SettingsService settings) =>
{
    var filter = new SeFilter(year ?? PhTime.Now.Year, type is "SPLV" or "SPHV" ? type : null, q);
    var entity = (await settings.LoadAsync()).EntityName;
    var name = $"{(filter.Category ?? "splv-sphv").ToLowerInvariant()}-deliveries-{filter.Year}";
    return format == "pdf"
        ? Results.File(await svc.RegisterPdfAsync(filter, entity, PreparedBy(http)), "application/pdf", name + ".pdf")
        : Results.File(await svc.RegisterExcelAsync(filter, entity), Xlsx, name + ".xlsx");
}).RequireAuthorization();

app.MapGet("/exports/reorder", async (bool? all, ReportService reports) =>
    Results.File(await reports.ReorderExcelAsync(all != true), Xlsx, $"reorder-list-{PhTime.Now:yyyyMMdd}.xlsx")).RequireAuthorization();

app.MapGet("/exports/stock-card/{id:int}", async (int id, ReportService reports) =>
    await reports.StockCardExcelAsync(id) is { } file
        ? Results.File(file, Xlsx, $"stock-card-{id}.xlsx")
        : Results.NotFound()).RequireAuthorization();

// Profile pictures
app.MapGet("/api/profile-picture/{fileName}", (string fileName) =>
{
    var path = pictureStorage.ResolveSafe(fileName);
    if (path is null || !File.Exists(path)) return Results.NotFound();

    var contentType = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
    return Results.File(File.OpenRead(path), contentType);
}).RequireAuthorization();

app.Run();

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

// ── Connection string (User Secrets in development; never in appsettings.json) ──
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:Default is empty. Run in the project folder: " +
        "dotnet user-secrets set \"ConnectionStrings:Default\" \"server=localhost;port=3306;database=ams_stockwatch;user=...;password=...;\"");

// ── Data Protection keys (cookies, antiforgery) stored in MySQL ─────────────────
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("AMS_StockWatch")
    .AddKeyManagementOptions(o => o.XmlRepository = new MySqlXmlRepository(connectionString));
// Encrypt the stored keys with Windows DPAPI on your own PC/server. Shared hosts often
// don't allow it, so it can be switched off with "DataProtection:UseDpapi": false.
if (OperatingSystem.IsWindows() && builder.Configuration.GetValue("DataProtection:UseDpapi", true))
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 50 * 1024 * 1024);   // PDF uploads

// One short-lived DbContext per operation (Blazor Server circuits are long-lived).
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
builder.Services.AddScoped<SettingsService>();
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
        // Secure-only cookies need HTTPS. "Auth:AllowHttpCookies": true is a TEMPORARY switch for a
        // server without an SSL certificate yet. Set it back to false once the site has HTTPS.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Configuration.GetValue("Auth:AllowHttpCookies", false)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;   // Strict drops the cookie when the site is opened from a link
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // Re-check the account every 5 minutes: deactivated / reset / changed password = signed out
        options.Events.OnValidatePrincipal = SessionGuard.ValidatePrincipalAsync;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
// Open pages re-check the account every 5 minutes too
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

// ── Sign-in rate limit per computer (IP address) ────────────────────────────
// 40 attempts per 5 minutes: plenty for a whole office behind one internet connection,
// far too few for automated password guessing. Each account also locks after 5 wrong tries.
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

// Pages show personal data; don't let the browser cache them.
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

// ── Security headers on every response ──────────────────────────────────────
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";                         // browsers must not guess file types
    h["X-Frame-Options"] = "SAMEORIGIN";                             // other websites can't show this site in a frame
    h["Content-Security-Policy"] = "frame-ancestors 'self'";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    await next();
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ── Sign in / out ───────────────────────────────────────────────────────────
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

// Re-issues the cookie after the profile, picture or password changes.
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

// ── Property documents (PDF) ────────────────────────────────────────────────
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

app.MapGet("/documents/download-bulk", async (string? ids, IDbContextFactory<AppDbContext> dbFactory) =>
{
    var idList = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
        .Where(n => n is not null).Select(n => n!.Value)
        .Distinct().Take(200).ToList();
    if (idList.Count == 0) return Results.BadRequest("No documents selected.");

    await using var db = await dbFactory.CreateDbContextAsync();
    var docs = await db.PropertyDocuments.Where(d => idList.Contains(d.DocumentId)).ToListAsync();

    var memoryStream = new MemoryStream();
    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            var path = DocPath(doc);
            if (path is null) continue;
            // Two files with the same original name would overwrite each other in the zip.
            var entryName = Path.GetFileName(doc.OriginalFileName);
            if (!usedNames.Add(entryName))
                entryName = $"{Path.GetFileNameWithoutExtension(entryName)} ({doc.DocumentId}){Path.GetExtension(entryName)}";
            var entry = archive.CreateEntry(entryName);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(path);
            await fileStream.CopyToAsync(entryStream);
        }
    }
    memoryStream.Position = 0;
    return Results.File(memoryStream, "application/zip", $"property-documents-{PhTime.Now:yyyyMMdd}.zip");
}).RequireAuthorization();

// ── Signed RIS (PDF uploaded at issuance) ───────────────────────────────────
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

// ── Catalog photos (admin preview) ──────────────────────────────────────────
app.MapGet("/catalog/image/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var img = await db.ItemImages.AsNoTracking().FirstOrDefaultAsync(i => i.ItemId == id);
    return img is null ? Results.NotFound() : Results.File(img.Content, img.ContentType);
}).RequireAuthorization();

// ── Excel downloads ─────────────────────────────────────────────────────────
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

app.MapGet("/exports/reorder", async (bool? all, ReportService reports) =>
    Results.File(await reports.ReorderExcelAsync(all != true), Xlsx, $"reorder-list-{PhTime.Now:yyyyMMdd}.xlsx")).RequireAuthorization();

app.MapGet("/exports/stock-card/{id:int}", async (int id, ReportService reports) =>
    await reports.StockCardExcelAsync(id) is { } file
        ? Results.File(file, Xlsx, $"stock-card-{id}.xlsx")
        : Results.NotFound()).RequireAuthorization();

// ── Profile pictures ────────────────────────────────────────────────────────
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

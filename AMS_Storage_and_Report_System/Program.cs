using System.IO.Compression;
using System.Security.Claims;
using AMS_Storage_and_Report_System.Components;
using AMS_Storage_and_Report_System.Components.Shared.Modals;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using AMS_Storage_and_Report_System.Services;
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
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);   // keys are no longer stored in plain text

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 50 * 1024 * 1024);   // PDF uploads

// One short-lived DbContext per operation (Blazor Server circuits are long-lived).
var serverVersion = ServerVersion.Parse("8.0.46-mysql");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseMySql(connectionString, serverVersion));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ModalService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/dashboard";
        options.Cookie.Name = "ams_stockwatch_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;   // Strict drops the cookie when the site is opened from a link
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<PropertyDocumentStorage>();
builder.Services.AddSingleton<ProfilePictureStorage>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

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
        ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddHours(8),
    });

    return result.User.RequirePasswordChange
        ? Results.LocalRedirect("/profile?requirePasswordChange=true")
        : Results.LocalRedirect(SafeLocal(returnUrl));
});

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
            ExpiresUtc = isPersistent ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddHours(8),
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

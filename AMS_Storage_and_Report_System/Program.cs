using AMS_Storage_and_Report_System.Components;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Default is 32 KB, which is too small for file uploads.
        options.MaximumReceiveMessageSize = 50 * 1024 * 1024; // 50 MB
    });

// --- Database ---
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("Default");
    options.UseMySql(connStr, ServerVersion.AutoDetect(connStr));
});

// --- Auth services ---
builder.Services.AddScoped<AuthService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Tell the browser never to cache pages from bfcache/back-forward navigation --
// otherwise hitting Back after logout can show a stale, still-"logged in" page
// instead of re-checking auth with the server.
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

// --- Login / logout endpoints ---
// These set the auth cookie over a real HTTP response, which is why they're
// plain minimal-API endpoints rather than Blazor event handlers -- a
// Blazor Server circuit (SignalR) can't set cookies after the page loads.
app.MapPost("/account/login", async (HttpContext http, AuthService authService, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);

    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var result = await authService.ValidateLoginAsync(username, password);

    if (!result.Succeeded || result.User is null)
    {
        var msg = Uri.EscapeDataString(result.ErrorMessage ?? "Login failed.");
        return Results.Redirect($"/login?Error={msg}");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, result.User.UserId.ToString()),
        new(ClaimTypes.Name, result.User.Username),
        new(ClaimTypes.Role, result.User.Role.ToString()),
        new("FullName", result.User.FullName)
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
    });

    return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/dashboard" : returnUrl);
});

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// --- Property document file endpoints ---
// Files are stored outside wwwroot, so these authenticated endpoints are the
// only way to reach them -- no static-file URL can serve them directly.
var propertyDocsPath = Path.Combine(app.Environment.ContentRootPath, "App_Data", "property-documents");
Directory.CreateDirectory(propertyDocsPath);

app.MapGet("/documents/download/{id:int}", async (int id, AppDbContext db) =>
{
    var doc = await db.PropertyDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var fullPath = Path.Combine(propertyDocsPath, doc.FilePath);
    if (!File.Exists(fullPath)) return Results.NotFound();

    return Results.File(fullPath, "application/pdf", doc.OriginalFileName);
}).RequireAuthorization();

app.MapGet("/documents/view/{id:int}", async (int id, AppDbContext db) =>
{
    var doc = await db.PropertyDocuments.FindAsync(id);
    if (doc is null) return Results.NotFound();

    var fullPath = Path.Combine(propertyDocsPath, doc.FilePath);
    if (!File.Exists(fullPath)) return Results.NotFound();

    return Results.File(File.OpenRead(fullPath), "application/pdf");
}).RequireAuthorization();

app.MapGet("/documents/download-bulk", async (string ids, AppDbContext db) =>
{
    var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(int.Parse)
                     .ToList();

    var docs = await db.PropertyDocuments
        .Where(d => idList.Contains(d.DocumentId))
        .ToListAsync();

    var memoryStream = new MemoryStream();
    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var doc in docs)
        {
            var fullPath = Path.Combine(propertyDocsPath, doc.FilePath);
            if (!File.Exists(fullPath)) continue;

            var entry = archive.CreateEntry(doc.OriginalFileName);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(fullPath);
            await fileStream.CopyToAsync(entryStream);
        }
    }

    memoryStream.Position = 0;
    return Results.File(memoryStream, "application/zip", "property-documents.zip");
}).RequireAuthorization();

app.Run();
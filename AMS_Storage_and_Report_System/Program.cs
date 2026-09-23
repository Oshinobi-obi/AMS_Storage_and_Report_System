using AMS_Storage_and_Report_System.Components;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var dpConnectionString = builder.Configuration.GetConnectionString("Default")!;

builder.Services.AddDataProtection()
    .SetApplicationName("AMS_StockWatch")
    .AddKeyManagementOptions(options =>
    {
        options.XmlRepository = new MySqlXmlRepository(dpConnectionString);
    });

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 50 * 1024 * 1024;
    });

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("Default");
    options.UseMySql(connStr, ServerVersion.AutoDetect(connStr));
});

builder.Services.AddHttpContextAccessor();
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

app.MapPost("/account/login", async (HttpContext http, AuthService authService, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);

    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var rememberMe = form.ContainsKey("rememberMe");

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
        new("FullName", result.User.FullName),
        new("DisplayName", result.User.DisplayName ?? ""),
        new("ProfilePicture", result.User.ProfilePicturePath ?? "")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = rememberMe,
        ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddHours(8)
    });

    // Check if user needs to change password
    if (result.User.RequirePasswordChange)
    {
        return Results.Redirect("/profile?requirePasswordChange=true");
    }

    return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/dashboard" : returnUrl);
});

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapGet("/account/refresh-session", async (HttpContext http, AppDbContext db, string? returnUrl) =>
{
    var username = http.User.Identity?.Name;
    if (string.IsNullOrEmpty(username))
    {
        return Results.Redirect("/login");
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
    if (user is null)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }

    // Create updated claims with fresh data from database
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role.ToString()),
        new("FullName", user.FullName),
        new("DisplayName", user.DisplayName ?? ""),
        new("ProfilePicture", user.ProfilePicturePath ?? "")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    // Get existing authentication properties to maintain session settings
    var authenticateResult = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var isPersistent = authenticateResult.Properties?.IsPersistent ?? false;

    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = isPersistent,
        ExpiresUtc = isPersistent ? DateTimeOffset.UtcNow.AddDays(30) : DateTimeOffset.UtcNow.AddHours(8)
    });

    return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/profile" : returnUrl);
}).RequireAuthorization();

var propertyDocsPath = app.Services.GetRequiredService<PropertyDocumentStorage>().RootPath;
var profilePicsPath = app.Services.GetRequiredService<ProfilePictureStorage>().RootPath;

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

app.MapGet("/api/profile-picture/{fileName}", async (string fileName) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.NotFound();

    var fullPath = Path.Combine(profilePicsPath, fileName);
    if (!File.Exists(fullPath))
        return Results.NotFound();

    var extension = Path.GetExtension(fileName).ToLowerInvariant();
    var contentType = extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    return Results.File(File.OpenRead(fullPath), contentType);
}).RequireAuthorization();

app.MapGet("/api/profile-picture/default", () =>
{
    return Results.NotFound();
});

app.Run();
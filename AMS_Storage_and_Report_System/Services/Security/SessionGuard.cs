// SessionGuard.cs — keeps sign-ins honest after the fact.
// A signed-in session is re-checked against the database (at most every 5 minutes):
// deactivated accounts, changed roles and changed/reset passwords end the session,
// both for normal page loads (cookie check) and for open pages (Blazor revalidation).

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services.Security;

public static class SessionGuard
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    public const string StampClaim = "pwd_stamp";
    private const string CheckedKey = "checked_at";

    /// A short fingerprint of the password hash. It changes whenever the password changes,
    /// without putting the hash itself in the cookie.
    public static string PasswordStamp(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];

    public static async Task<bool> IsStillValidAsync(ClaimsPrincipal principal, IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        if (!uint.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var u = await db.Users.AsNoTracking().Where(x => x.UserId == id)
            .Select(x => new { x.IsActive, x.Role, x.PasswordHash }).FirstOrDefaultAsync(ct);

        return u is not null
            && u.IsActive
            && u.Role is UserRole.SuperAdmin or UserRole.Admin
            && principal.FindFirstValue(ClaimTypes.Role) == u.Role.ToString()
            && principal.FindFirstValue(StampClaim) == PasswordStamp(u.PasswordHash);
    }

    /// Cookie check on every request (throttled to one database query per 5 minutes per user).
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null) return;
        var props = context.Properties;
        if (props.Items.TryGetValue(CheckedKey, out var last) &&
            DateTimeOffset.TryParse(last, out var at) && DateTimeOffset.UtcNow - at < CheckEvery)
            return;

        bool valid;
        try
        {
            var dbFactory = context.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
            valid = await IsStillValidAsync(context.Principal, dbFactory, context.HttpContext.RequestAborted);
        }
        catch (Exception)
        {
            return;   // database hiccup: don't sign everyone out, try again on the next request
        }

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        props.Items[CheckedKey] = DateTimeOffset.UtcNow.ToString("O");
        context.ShouldRenew = true;
    }
}

/// Re-checks the signed-in user of an OPEN page every 5 minutes, so a deactivated
/// account is sent to the sign-in page even if it never reloads.
public sealed class RevalidatingAuthStateProvider(ILoggerFactory loggerFactory, IDbContextFactory<AppDbContext> dbFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => SessionGuard.CheckEvery;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        if (state.User.Identity?.IsAuthenticated != true) return true;   // guests: nothing to check
        try { return await SessionGuard.IsStillValidAsync(state.User, dbFactory, ct); }
        catch (Exception) { return true; }   // keep the page usable during a database hiccup
    }
}

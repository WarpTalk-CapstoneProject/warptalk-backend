using System.Security.Claims;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.Gateway.Platform;

/// <summary>
/// Maintenance mode (platform setting <c>general.maintenance.enabled</c>), enforced at the one door
/// every API call and hub connection passes through.
///
/// While it is on, a caller whose e-mail is not on <c>general.maintenance.allowlist_emails</c> gets
/// 503 with the maintenance message (errorCode MAINTENANCE, Retry-After) instead of the proxied
/// call. What stays open, and why:
/// <list type="bullet">
/// <item>/health* — an orchestrator must not read maintenance as a dead gateway and restart it.</item>
/// <item>/api/v1/platform/status — how the web learns to show the banner.</item>
/// <item>/api/v1/auth/* — staff must be able to sign in to turn maintenance off again.</item>
/// <item>/api/v1/admin/* — the portal itself; every endpoint behind it still needs a staff permission.</item>
/// </list>
/// The switch is read synchronously from the process's last settings snapshot (refreshed in the
/// background every few seconds), so this adds no Redis round-trip to any request.
/// </summary>
public sealed class MaintenanceModeMiddleware
{
    public const string ErrorCode = "MAINTENANCE";
    public const int RetryAfterSeconds = 300;

    private readonly RequestDelegate _next;

    public MaintenanceModeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IPlatformSettings settings)
    {
        if (IsExempt(context.Request.Path)
            || !settings.GetBoolean(PlatformSettingsCatalog.MaintenanceEnabled)
            || IsAllowlisted(context.User, settings.GetStringList(PlatformSettingsCatalog.MaintenanceAllowlist)))
        {
            await _next(context);
            return;
        }

        var message = settings.GetString(PlatformSettingsCatalog.MaintenanceMessage);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new { error = message, errorCode = ErrorCode, message });
    }

    public static bool IsExempt(PathString path)
        => path.StartsWithSegments("/health")
           || path.StartsWithSegments(PlatformStatusEndpoints.Path)
           || path.StartsWithSegments("/api/v1/auth")
           || path.StartsWithSegments("/api/v1/admin");

    public static bool IsAllowlisted(ClaimsPrincipal user, IReadOnlyList<string> allowlist)
    {
        if (allowlist.Count == 0 || user.Identity?.IsAuthenticated != true) return false;
        var email = user.GetEmail();
        return !string.IsNullOrWhiteSpace(email)
               && allowlist.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// GET /api/v1/platform/status — anonymous, and the only platform settings anyone outside the
/// portal can read: whether maintenance is on and its message, the support address, and whether
/// "Sign in with Google" should be offered. Nothing here identifies a person (the maintenance
/// allowlist is never returned).
/// </summary>
public static class PlatformStatusEndpoints
{
    public const string Path = "/api/v1/platform/status";

    public sealed record MaintenanceStatus(bool Enabled, string? Message);

    public sealed record PlatformStatus(MaintenanceStatus Maintenance, string SupportEmail, bool GoogleSignInEnabled);

    public static async Task<PlatformStatus> ReadAsync(IPlatformSettings settings, CancellationToken ct)
    {
        var enabled = await settings.GetBooleanAsync(PlatformSettingsCatalog.MaintenanceEnabled, ct: ct);
        return new PlatformStatus(
            new MaintenanceStatus(enabled, enabled ? await settings.GetStringAsync(PlatformSettingsCatalog.MaintenanceMessage, ct: ct) : null),
            await settings.GetStringAsync(PlatformSettingsCatalog.SupportEmail, ct: ct),
            await settings.GetBooleanAsync(PlatformSettingsCatalog.GoogleSignInEnabled, ct: ct));
    }

    public static IEndpointRouteBuilder MapPlatformStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (IPlatformSettings settings, HttpContext context, CancellationToken ct) =>
            {
                // Short and shared: every open tab polls this, and a change should still show within a minute.
                context.Response.Headers.CacheControl = "public, max-age=15";
                return Results.Ok(await ReadAsync(settings, ct));
            })
            .AllowAnonymous();
        return endpoints;
    }
}

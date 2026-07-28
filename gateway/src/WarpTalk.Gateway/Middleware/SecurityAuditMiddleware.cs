using System.Diagnostics;
using System.Security.Claims;

namespace WarpTalk.Gateway.Middleware;

public sealed class SecurityAuditMiddleware(
    RequestDelegate next,
    ILogger<SecurityAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsSensitivePath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub")
                ?? "anonymous";
            var workspaceId = context.Request.Headers["X-Workspace-Id"].FirstOrDefault();
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            logger.LogInformation(
                "security_audit actor_id={ActorId} workspace_id={WorkspaceId} method={Method} path={Path} status_code={StatusCode} remote_ip={RemoteIp} trace_id={TraceId} duration_ms={DurationMs} outcome={Outcome}",
                userId,
                workspaceId,
                context.Request.Method,
                context.Request.Path.Value,
                failure is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError,
                context.Connection.RemoteIpAddress?.ToString(),
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                elapsedMs,
                failure is null ? "completed" : "failed");
        }
    }

    private static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/api/v1/admin")
        || path.StartsWithSegments("/api/v1/billing")
        || path.StartsWithSegments("/api/v1/credits")
        || path.StartsWithSegments("/api/v1/subscriptions")
        || path.Value?.Contains("/documents", StringComparison.OrdinalIgnoreCase) == true;
}

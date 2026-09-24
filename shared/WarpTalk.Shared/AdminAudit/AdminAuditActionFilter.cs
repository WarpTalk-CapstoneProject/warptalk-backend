using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.Shared.AdminAudit;

/// <summary>
/// Arms <see cref="AdminAuditScope"/> for an <see cref="AdminAuditedAttribute"/> endpoint called by
/// a platform administrator, then records how the call ended.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The success entry is normally written by <see cref="AdminAuditSaveChangesInterceptor"/>
/// BEFORE the change commits. Only a successful call that saved nothing it names (an idempotent
/// repeat) is recorded here, afterwards and without a diff.</item>
/// <item>A call that ends in an error is recorded as <c>failed</c>, with the error text the caller
/// was given, under <c>{correlation}:failed</c>. If the interceptor had already recorded the
/// attempt, the store's reader treats that entry as superseded by this one.</item>
/// </list>
/// Both are best-effort: by the time the filter runs, the request's outcome is settled, and a
/// failure to describe it must not turn a done change into an error.
/// </remarks>
public sealed class AdminAuditActionFilter : IAsyncActionFilter
{
    public const string FailedSuffix = ":failed";
    private const int MaxCorrelationLength = 100;
    private const int MaxReasonLength = 1000;

    private readonly AdminAuditScope _scope;
    private readonly IAdminAuditSink _sink;
    private readonly ILogger<AdminAuditActionFilter> _logger;

    public AdminAuditActionFilter(AdminAuditScope scope, IAdminAuditSink sink, ILogger<AdminAuditActionFilter> logger)
    {
        _scope = scope;
        _sink = sink;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attribute = context.ActionDescriptor.EndpointMetadata.OfType<AdminAuditedAttribute>().FirstOrDefault();
        var httpContext = context.HttpContext;

        if (attribute is null
            || !IsPlatformAdministrator(httpContext.User)
            || !AdminActorContext.TryResolve(httpContext.User, httpContext, out var actor))
        {
            await next();
            return;
        }

        var (entityId, entityKey) = ReadSubject(context, attribute.EntityRouteKey);
        var invocation = new AdminAuditInvocation
        {
            Action = attribute.Action,
            EntityType = attribute.EntityType,
            SubjectTypes = attribute.SubjectTypes,
            ActorId = actor.ActorId,
            CorrelationId = actor.CorrelationId,
            Reason = FindReason(context),
            RouteEntityId = entityId,
            RouteEntityKey = entityKey,
            RouteWorkspaceId = ReadGuid(context, attribute.WorkspaceRouteKey),
            Metadata = AdminAuditRequestMetadata.FromHttpContext(httpContext),
            Aggregate = attribute.Aggregate,
        };
        _scope.Arm(invocation);

        var executed = await next();

        var status = StatusOf(executed);
        var ct = httpContext.RequestAborted;
        if (status >= 400)
        {
            var failed = FailedFrom(invocation, ErrorOf(executed, status));
            foreach (var record in failed)
            {
                if (!await _sink.RecordAsync(record, CancellationToken.None))
                {
                    _logger.LogError(
                        "A failed admin action could not be recorded. Action: {Action}, CorrelationId: {CorrelationId}",
                        record.Action, record.CorrelationId);
                }
            }
        }
        else if (!invocation.HasRecorded && !ct.IsCancellationRequested)
        {
            var record = invocation.ToRecord(AdminAuditResults.Succeeded, null, invocation.CorrelationId);
            if (!await _sink.RecordAsync(record, CancellationToken.None))
            {
                _logger.LogError(
                    "A completed admin action could not be recorded. Action: {Action}, CorrelationId: {CorrelationId}",
                    record.Action, record.CorrelationId);
            }
        }
    }

    /// <summary>
    /// The failed entries for a call: one per subject the interceptor already recorded (so each
    /// supersedes its own attempt), or one from the route when nothing had been saved.
    /// </summary>
    public static IReadOnlyList<AdminAuditRecord> FailedFrom(AdminAuditInvocation invocation, string? error)
    {
        if (invocation.HasRecorded)
        {
            return invocation.Recorded
                .Select(record => record with
                {
                    Result = AdminAuditResults.Failed,
                    ErrorMessage = error,
                    CorrelationId = FailedCorrelation(record.CorrelationId),
                })
                .ToList();
        }

        return [invocation.ToRecord(AdminAuditResults.Failed, error, FailedCorrelation(invocation.CorrelationId))];
    }

    public static string FailedCorrelation(string correlationId)
    {
        var head = correlationId.Length + FailedSuffix.Length > MaxCorrelationLength
            ? correlationId[..(MaxCorrelationLength - FailedSuffix.Length)]
            : correlationId;
        return head + FailedSuffix;
    }

    /// <summary>
    /// The exact platform role <c>admin</c> (see <see cref="SystemAdminHandler"/>) or the global
    /// <c>Admin</c> role that <c>Roles = AdminSystem</c> routes also admit. Workspace roles are not
    /// in the token, so a workspace owner on a shared route never matches.
    /// </summary>
    public static bool IsPlatformAdministrator(ClaimsPrincipal user) =>
        user.Identities.Any(identity => identity.IsAuthenticated
            && identity.Claims.Any(claim =>
                (claim.Type == identity.RoleClaimType || claim.Type is "role" or "roles")
                && claim.Value is WorkspaceRoleConstants.SystemAdmin or WorkspaceRoleConstants.Admin));

    private static int StatusOf(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled) return 500;
        return executed.Result switch
        {
            IStatusCodeActionResult { StatusCode: { } code } => code,
            ObjectResult => 200,
            null => executed.HttpContext.Response.StatusCode,
            _ => 200,
        };
    }

    private static string? ErrorOf(ActionExecutedContext executed, int status)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return "The request failed with an unexpected error.";

        var value = (executed.Result as ObjectResult)?.Value;
        var text = value switch
        {
            ApiErrorResponse error => error.Error,
            ProblemDetails problem => problem.Detail ?? problem.Title,
            string message => message,
            null => null,
            _ => ReadStringProperty(value, "Error") ?? ReadStringProperty(value, "Message"),
        };
        return string.IsNullOrWhiteSpace(text) ? $"HTTP {status}" : text;
    }

    /// <summary>
    /// The reason the admin gave: a <c>reason</c> argument, a <c>Reason</c> on the body, or
    /// <c>?reason=</c>. Nothing is invented when there is none.
    /// </summary>
    private static string? FindReason(ActionExecutingContext context)
    {
        foreach (var (name, value) in context.ActionArguments)
        {
            var reason = value switch
            {
                string text when string.Equals(name, "reason", StringComparison.OrdinalIgnoreCase) => text,
                null or string => null,
                _ => ReadStringProperty(value, "Reason"),
            };
            if (!string.IsNullOrWhiteSpace(reason)) return Bound(reason);
        }

        var query = context.HttpContext.Request.Query["reason"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : Bound(query);
    }

    private static string Bound(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength];
    }

    private static string? ReadStringProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property?.PropertyType == typeof(string) ? property.GetValue(value) as string : null;
    }

    private static (Guid? Id, string? Key) ReadSubject(ActionExecutingContext context, string? routeKey)
    {
        if (routeKey is null || !context.RouteData.Values.TryGetValue(routeKey, out var raw) || raw is null)
            return (null, null);
        var text = raw.ToString();
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        return Guid.TryParse(text, out var id) ? (id, null) : (null, AdminAuditRequestMetadata.Bound(text, 100));
    }

    private static Guid? ReadGuid(ActionExecutingContext context, string routeKey) =>
        context.RouteData.Values.TryGetValue(routeKey, out var raw) && Guid.TryParse(raw?.ToString(), out var id)
            ? id
            : null;
}

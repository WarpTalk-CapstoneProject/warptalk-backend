using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// Who performed an admin mutation, and under which correlation id (WT-205).
/// </summary>
/// <remarks>
/// The actor is resolved from authenticated claims only. No admin endpoint may accept an actor
/// id, workspace id, or role from the request body or query string — a client-supplied actor
/// would make the audit trail forgeable.
/// </remarks>
public readonly record struct AdminActorContext(Guid ActorId, string CorrelationId)
{
    /// <summary>Header clients and the gateway may use to thread a correlation id through.</summary>
    public const string CorrelationHeader = "X-Correlation-ID";

    private const int MaxCorrelationLength = 100;

    /// <summary>
    /// Returns false when the token carries no usable subject, which the caller should surface
    /// as 401 rather than falling back to a placeholder actor.
    /// </summary>
    public static bool TryResolve(ClaimsPrincipal user, HttpContext httpContext, out AdminActorContext actor)
    {
        var actorId = user.GetUserId();
        if (actorId is null)
        {
            actor = default;
            return false;
        }

        actor = new AdminActorContext(actorId.Value, ResolveCorrelationId(httpContext));
        return true;
    }

    private static string ResolveCorrelationId(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers[CorrelationHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return httpContext.TraceIdentifier;
        }

        // Client-supplied and only ever written to logs and the audit trail, so bound the
        // length rather than trusting it.
        var trimmed = header.Trim();
        return trimmed.Length <= MaxCorrelationLength
            ? trimmed
            : trimmed[..MaxCorrelationLength];
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.AdminAudit;

/// <summary>
/// Who asked, from where, as the admin's own HTTP request says it: the e-mail the token carried,
/// the first X-Forwarded-For hop and the user agent.
/// </summary>
/// <remarks>
/// Read at the service the admin called, never at the audit store. By the time an entry reaches
/// the store over gRPC the request in hand is a service-to-service call, whose address is a
/// container and whose user agent is grpc-dotnet — both true, and both useless in an audit trail.
/// </remarks>
public sealed record AdminAuditRequestMetadata(
    string? ActorEmail,
    string? ActorName,
    string? IpAddress,
    string? UserAgent)
{
    public const int MaxIpLength = 64;
    public const int MaxUserAgentLength = 512;
    public const int MaxEmailLength = 320;
    public const int MaxNameLength = 200;

    public static readonly AdminAuditRequestMetadata Empty = new(null, null, null, null);

    /// <summary>
    /// Null when there is no request, or when the request is itself a gRPC call — that one's
    /// address and agent belong to the calling service, not to a person.
    /// </summary>
    public static AdminAuditRequestMetadata FromHttpContext(HttpContext? httpContext)
    {
        if (httpContext is null || IsGrpcRequest(httpContext.Request)) return Empty;

        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        var ip = string.IsNullOrWhiteSpace(forwarded)
            ? httpContext.Connection.RemoteIpAddress?.ToString()
            : forwarded.Split(',')[0].Trim();

        return new AdminAuditRequestMetadata(
            Bound(httpContext.User.GetEmail(), MaxEmailLength),
            Bound(ActorNameOf(httpContext.User), MaxNameLength),
            Bound(ip, MaxIpLength),
            Bound(httpContext.Request.Headers.UserAgent.ToString(), MaxUserAgentLength));
    }

    /// <summary>Copies the fields onto an outgoing record request, leaving any the caller already set.</summary>
    public void ApplyTo(RecordAdminActionRequest request)
    {
        if (string.IsNullOrEmpty(request.ActorEmail) && ActorEmail is not null) request.ActorEmail = ActorEmail;
        if (string.IsNullOrEmpty(request.ActorName) && ActorName is not null) request.ActorName = ActorName;
        if (string.IsNullOrEmpty(request.IpAddress) && IpAddress is not null) request.IpAddress = IpAddress;
        if (string.IsNullOrEmpty(request.UserAgent) && UserAgent is not null) request.UserAgent = UserAgent;
    }

    public static bool IsGrpcRequest(HttpRequest request) =>
        request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Access tokens carry no name today; a token that does is used, and nothing is invented.</summary>
    private static string? ActorNameOf(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("name")?.Value;

    public static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

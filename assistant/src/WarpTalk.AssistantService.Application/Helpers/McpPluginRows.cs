using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Validation and construction of a <c>kind='mcp'</c> catalog row, shared by the system admin's
/// marketplace create and a workspace Owner's private plugin.
/// </summary>
/// <remarks>
/// One copy on purpose. The key is also the row's OAuth provider identity, so the collision checks
/// here are what stop a new row from being handed an existing grant - a second, drifting copy of
/// them is a way to hand one out.
/// </remarks>
public static class McpPluginRows
{
    public const int MaxLabelLength = 150;
    public const int MaxDescriptionLength = 500;
    public const int MaxUrlLength = 1000;
    private const int MaxKeySlugLength = 40;

    /// <summary>The checks a new MCP row's key and URL have to pass. The error code is the caller's.</summary>
    public static async Task<Result> ValidateNewAsync(
        IUnitOfWork unitOfWork,
        string key,
        string? mcpServerUrl,
        string errorCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure("A plugin key is required.", errorCode);

        // Some keys collide with a literal route segment sitting beside a {pluginKey} route, and
        // ASP.NET gives the literal precedence - so the row is not rejected by routing, it is
        // silently unreachable. The database rejects these too; catching it here gives a message.
        if (PluginConstants.IsReservedPluginKey(key))
            return Result.Failure($"'{key.Trim()}' is reserved and cannot be a plugin key.", errorCode);

        var url = ValidateServerUrl(mcpServerUrl, errorCode);
        if (!url.IsSuccess) return url;

        if (await unitOfWork.PluginRepository.AnyAsync(p => p.PluginKey == key, ct))
            return Result.Failure($"A plugin keyed '{key}' already exists.", errorCode);

        // An MCP row takes its key as its provider, and the provider is the identity of a user's
        // OAuth grant (20260907101000). A row keyed 'google' would be handed the existing Google
        // connection, refresh token and all.
        if (await unitOfWork.PluginRepository.AnyAsync(p => p.Provider == key, ct))
            return Result.Failure(
                $"'{key}' is already in use as a provider by another plugin; an MCP plugin needs a provider of its own.",
                errorCode);

        return Result.Success();
    }

    public static Result ValidateServerUrl(string? mcpServerUrl, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(mcpServerUrl)
            || mcpServerUrl.Length > MaxUrlLength
            || !Uri.TryCreate(mcpServerUrl, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure("An MCP plugin needs an absolute https:// server URL.", errorCode);
        }

        return Result.Success();
    }

    /// <summary>
    /// The extra bar a workspace Owner's URL has to clear, on top of <see cref="ValidateServerUrl"/>.
    /// </summary>
    /// <remarks>
    /// A system admin's URL is trusted; an Owner's is not. The assistant service fetches this URL
    /// itself - discovery, registration, tools/list - from inside the platform's private network, so
    /// a URL naming a private address turns "add a plugin" into a way to make the service call
    /// internal hosts. Refused here: IP literals in loopback, private, link-local, CGNAT and unique
    /// local ranges, <c>localhost</c>, single-label hosts, and the internal-only suffixes.
    /// <para>
    /// This is a syntactic check on the URL as written. A public hostname that RESOLVES to a private
    /// address is not caught here; that needs the HTTP client to check the connected address, which
    /// belongs with the MCP transport rather than with row validation.
    /// </para>
    /// </remarks>
    public static Result ValidatePublicServerUrl(string? mcpServerUrl, string errorCode)
    {
        var basic = ValidateServerUrl(mcpServerUrl, errorCode);
        if (!basic.IsSuccess) return basic;

        var host = new Uri(mcpServerUrl!).IdnHost.TrimEnd('.').ToLowerInvariant();
        var refusal = Result.Failure("The MCP server URL must be a public https:// address.", errorCode);

        if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
            return refusal;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return IsPublicAddress(address) ? Result.Success() : refusal;

        if (!host.Contains('.')) return refusal;

        foreach (var suffix in new[] { ".local", ".internal", ".lan", ".home", ".corp", ".intranet" })
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal)) return refusal;
        }

        return Result.Success();
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);
        }

        return false;
    }

    /// <summary>
    /// A new row, not installed and not connected. Tools stay empty until the first connect: for an
    /// MCP row that column is a cache of tools/list, not something anyone authors.
    /// </summary>
    public static Plugin Build(
        string key,
        string label,
        string description,
        string mcpServerUrl,
        string? avatarUrl,
        IReadOnlyList<string>? requiredScopes,
        Guid? ownerWorkspaceId,
        Guid? createdBy,
        DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            PluginKey = key,
            Label = label,
            Description = description,
            AvatarUrl = avatarUrl,
            // Its own provider, taken from its own key: each MCP server is a separate authorization
            // server with a separate grant. ValidateNewAsync is what keeps that true.
            Provider = key,
            RequiredScopesJson = JsonSerializer.Serialize(requiredScopes ?? Array.Empty<string>()),
            ToolsJson = "[]",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = mcpServerUrl,
            OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
            IsActive = true,
            OwnerWorkspaceId = ownerWorkspaceId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>
    /// A globally unique key for a private plugin, derived from its label.
    /// </summary>
    /// <remarks>
    /// Never chosen by the Owner, because the key is also the provider: <c>ws_</c> keeps it out of
    /// every reserved route segment and every native provider name, and the random suffix keeps two
    /// workspaces' "Internal CRM" apart. ASCII only, so a Vietnamese label still yields a readable key.
    /// </remarks>
    public static string DerivePrivateKey(string label)
    {
        var decomposed = label.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(ch);
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        }

        var body = slug.ToString().Trim('_');
        if (body.Length > MaxKeySlugLength) body = body[..MaxKeySlugLength].Trim('_');
        if (body.Length == 0) body = "plugin";

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{WorkspacePluginConstants.PrivatePluginKeyPrefix}{body}_{suffix}";
    }
}

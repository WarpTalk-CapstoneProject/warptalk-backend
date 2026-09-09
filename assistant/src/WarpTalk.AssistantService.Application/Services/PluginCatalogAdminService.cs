using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IPluginCatalogAdminService"/>
/// <remarks>
/// The invariant to hold while reading this class: <see cref="Plugin.OAuthClientSecretEncrypted"/>
/// is written here and read nowhere. It never reaches a DTO, never reaches a log line, and is not
/// decrypted on any path in this file - <see cref="IPluginCredentialProtector.Unprotect"/> is not
/// called once. The only thing an operator can learn about a secret is whether the column is null.
/// </remarks>
public class PluginCatalogAdminService : IPluginCatalogAdminService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginCredentialProtector _credentialProtector;

    private const int MaxLabelLength = 150;
    private const int MaxDescriptionLength = 500;
    private const int MaxUrlLength = 1000;
    private const int MaxCategoryLength = 50;
    private const int MaxClientIdLength = 500;
    private const int DefaultAuditPageSize = 50;
    private const int MaxAuditPageSize = 200;

    public PluginCatalogAdminService(IUnitOfWork unitOfWork, IPluginCredentialProtector credentialProtector)
    {
        _unitOfWork = unitOfWork;
        _credentialProtector = credentialProtector;
    }

    public async Task<Result<IReadOnlyList<PluginCatalogAdminListItemDto>>> ListAsync(CancellationToken ct = default)
    {
        // Unfiltered on purpose. The user-facing catalog shows only is_active rows, which makes a
        // retired row invisible in the one place someone would go to un-retire it.
        var plugins = await _unitOfWork.PluginRepository.GetAllAsync(ct: ct);
        var installationCounts = await _unitOfWork.PluginInstallationRepository.CountByPluginAsync(ct);

        var items = plugins
            // sort_order is the curated order the catalog page renders; label breaks the tie for
            // the rows nobody has curated, which all default to 0.
            .OrderBy(plugin => plugin.SortOrder)
            .ThenBy(plugin => plugin.Label, StringComparer.Ordinal)
            .Select(plugin => new PluginCatalogAdminListItemDto(
                plugin.PluginKey,
                plugin.Label,
                plugin.Description,
                plugin.Kind,
                plugin.Provider,
                plugin.IsActive,
                plugin.IsFeatured,
                plugin.SortOrder,
                plugin.Category,
                plugin.OAuthClientSource,
                !string.IsNullOrWhiteSpace(plugin.OAuthClientId),
                !string.IsNullOrWhiteSpace(plugin.OAuthClientSecretEncrypted),
                ReadTools(plugin).Count,
                installationCounts.TryGetValue(plugin.Id, out var count) ? count : 0))
            .ToList();

        return Result.Success<IReadOnlyList<PluginCatalogAdminListItemDto>>(items);
    }

    public async Task<Result<PluginCatalogAdminDetailDto>> GetAsync(string pluginKey, CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogAdminDetailDto>(pluginKey);

        return Result.Success(await ToDetailAsync(plugin, ct));
    }

    public async Task<Result<PluginCatalogAdminDetailDto>> UpdateAsync(
        string pluginKey,
        UpdatePluginCatalogRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogAdminDetailDto>(pluginKey);

        var errors = new List<string>();

        // Validation runs to completion before a single field is written. The entity is tracked, so
        // assigning as we go would leave half an invalid edit staged on it - harmless while a
        // rejected request ends the scope, and a silent partial write the first time anything else
        // in the same scope calls SaveChangesAsync. Collecting the writes as closures keeps
        // "is this edit legal" and "apply this edit" from interleaving at all.
        var edits = new List<Action<Plugin>>();

        if (request.Label is not null)
        {
            var label = request.Label.Trim();
            if (label.Length == 0) errors.Add("'label' cannot be blank.");
            else if (label.Length > MaxLabelLength) errors.Add($"'label' must be at most {MaxLabelLength} characters.");
            else edits.Add(row => row.Label = label);
        }

        if (request.Description is not null)
        {
            var description = request.Description.Trim();
            if (description.Length == 0) errors.Add("'description' cannot be blank.");
            else if (description.Length > MaxDescriptionLength) errors.Add($"'description' must be at most {MaxDescriptionLength} characters.");
            else edits.Add(row => row.Description = description);
        }

        if (request.AvatarUrl is not null)
        {
            // Empty clears; see UpdatePluginCatalogRequest. Both an absolute URL and a site-relative
            // path are accepted because the catalog already holds both - the Google rows point at
            // /assets/plugins/*.svg while an MCP row usually points at the vendor's own CDN.
            var avatarUrl = request.AvatarUrl.Trim();
            if (avatarUrl.Length == 0) edits.Add(row => row.AvatarUrl = null);
            else if (avatarUrl.Length > MaxUrlLength) errors.Add($"'avatarUrl' must be at most {MaxUrlLength} characters.");
            else if (!IsHttpUrl(avatarUrl) && !avatarUrl.StartsWith('/')) errors.Add("'avatarUrl' must be an http(s) URL or a site-relative path beginning with '/'.");
            else edits.Add(row => row.AvatarUrl = avatarUrl);
        }

        if (request.McpServerUrl is not null)
        {
            var serverUrl = request.McpServerUrl.Trim();
            if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal))
            {
                // A native row is served by compiled-in code that never reads this column, so
                // accepting a value here would look like a fix and change nothing.
                errors.Add($"'mcpServerUrl' applies only to a kind='{PluginConstants.PluginKind.Mcp}' row; '{plugin.PluginKey}' is '{plugin.Kind}'.");
            }
            else if (serverUrl.Length == 0)
            {
                // plugins_mcp_requires_server_url would reject this at the database; saying so here
                // gives a message instead of a 500.
                errors.Add("'mcpServerUrl' cannot be cleared on an MCP row.");
            }
            else if (serverUrl.Length > MaxUrlLength)
            {
                errors.Add($"'mcpServerUrl' must be at most {MaxUrlLength} characters.");
            }
            else if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) || serverUri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add("'mcpServerUrl' must be an absolute https:// URL.");
            }
            else
            {
                edits.Add(row => row.McpServerUrl = serverUrl);
            }
        }

        if (request.RequiredScopes is not null)
        {
            var scopes = new List<string>();
            foreach (var scope in request.RequiredScopes)
            {
                var trimmed = scope?.Trim() ?? string.Empty;
                if (trimmed.Length == 0) errors.Add("'requiredScopes' contains a blank entry.");
                else if (!scopes.Contains(trimmed, StringComparer.Ordinal)) scopes.Add(trimmed);
            }

            var scopesJson = JsonSerializer.Serialize(scopes);
            edits.Add(row => row.RequiredScopesJson = scopesJson);
        }

        if (request.Category is not null)
        {
            var category = request.Category.Trim();
            if (category.Length == 0) edits.Add(row => row.Category = null);
            else if (category.Length > MaxCategoryLength) errors.Add($"'category' must be at most {MaxCategoryLength} characters.");
            else edits.Add(row => row.Category = category);
        }

        if (request.IsActive is { } isActive) edits.Add(row => row.IsActive = isActive);
        if (request.IsFeatured is { } isFeatured) edits.Add(row => row.IsFeatured = isFeatured);
        if (request.SortOrder is { } sortOrder) edits.Add(row => row.SortOrder = sortOrder);

        if (errors.Count > 0)
            return Invalid<PluginCatalogAdminDetailDto>(errors, PluginConstants.ErrorCodes.InvalidCatalogUpdate);

        foreach (var edit in edits) edit(plugin);

        await StampAndSaveAsync(plugin, adminUserId, ct);
        return Result.Success(await ToDetailAsync(plugin, ct));
    }

    public async Task<Result<PluginCatalogAdminDetailDto>> SetOAuthClientAsync(
        string pluginKey,
        SetPluginOAuthClientRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogAdminDetailDto>(pluginKey);

        // A native row's OAuth client comes from configuration, not from this column - see
        // GoogleWorkspaceApiOptions. Writing one here would be inert, and an operator who did it
        // while chasing an empty client id in production would believe the problem was fixed.
        if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal))
        {
            return Invalid<PluginCatalogAdminDetailDto>(
                [
                    $"'{plugin.PluginKey}' is a '{plugin.Kind}' plugin: its OAuth client is read from service "
                    + "configuration, not from the catalog row. Set it in the environment instead.",
                ],
                PluginConstants.ErrorCodes.InvalidCatalogUpdate);
        }

        var clientId = request.ClientId?.Trim() ?? string.Empty;
        var errors = new List<string>();

        // plugins_preregistered_requires_client_id makes a blank id unstorable at this source.
        if (clientId.Length == 0) errors.Add("'clientId' is required.");
        else if (clientId.Length > MaxClientIdLength) errors.Add($"'clientId' must be at most {MaxClientIdLength} characters.");

        foreach (var (name, value) in new[]
                 {
                     ("authorizationEndpoint", request.AuthorizationEndpoint),
                     ("tokenEndpoint", request.TokenEndpoint),
                     ("revokeEndpoint", request.RevokeEndpoint),
                 })
        {
            if (value is null) continue;
            var endpoint = value.Trim();
            if (endpoint.Length == 0) continue;
            if (endpoint.Length > MaxUrlLength) errors.Add($"'{name}' must be at most {MaxUrlLength} characters.");
            else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                errors.Add($"'{name}' must be an absolute https:// URL.");
        }

        if (errors.Count > 0)
            return Invalid<PluginCatalogAdminDetailDto>(errors, PluginConstants.ErrorCodes.InvalidCatalogUpdate);

        plugin.OAuthClientId = clientId;
        plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Preregistered;

        // Tri-state, per SetPluginOAuthClientRequest: null leaves the stored secret alone, empty
        // clears it, a value replaces it. Leaving it alone is the default because no endpoint hands
        // a secret back, so an operator rotating only the id has no way to re-send one.
        if (request.ClientSecret is { } clientSecret)
        {
            plugin.OAuthClientSecretEncrypted = string.IsNullOrWhiteSpace(clientSecret)
                ? null
                : _credentialProtector.Protect(clientSecret);
        }

        // Supplied endpoints override discovery; omitted ones leave whatever discovery already
        // cached, so rotating a client id does not silently force a re-discovery.
        if (!string.IsNullOrWhiteSpace(request.AuthorizationEndpoint)) plugin.OAuthAuthorizationEndpoint = request.AuthorizationEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(request.TokenEndpoint)) plugin.OAuthTokenEndpoint = request.TokenEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(request.RevokeEndpoint)) plugin.OAuthRevokeEndpoint = request.RevokeEndpoint.Trim();

        await StampAndSaveAsync(plugin, adminUserId, ct);
        return Result.Success(await ToDetailAsync(plugin, ct));
    }

    public async Task<Result<PluginCatalogAdminDetailDto>> ReplaceToolsAsync(
        string pluginKey,
        ReplacePluginToolsRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogAdminDetailDto>(pluginKey);

        var (errors, tools) = PluginToolManifestValidator.Validate(plugin.PluginKey, request.Tools);
        if (errors.Count > 0)
            return Invalid<PluginCatalogAdminDetailDto>(errors, PluginConstants.ErrorCodes.InvalidToolManifest);

        plugin.ToolsJson = JsonSerializer.Serialize(tools, PluginDefinitionMapper.JsonOptions);

        // tools_synced_at means "this is what the MCP server last told us its tools were". An
        // operator-authored manifest is not that, so the marker is cleared rather than refreshed -
        // otherwise a hand edit would masquerade as a successful tools/list.
        plugin.ToolsSyncedAt = null;
        plugin.ToolsManifestHash = null;

        await StampAndSaveAsync(plugin, adminUserId, ct);
        return Result.Success(await ToDetailAsync(plugin, ct));
    }

    public async Task<Result<PluginCatalogAdminDetailDto>> RediscoverAsync(
        string pluginKey,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogAdminDetailDto>(pluginKey);

        if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal))
        {
            return Invalid<PluginCatalogAdminDetailDto>(
                [
                    $"Discovery applies only to a kind='{PluginConstants.PluginKind.Mcp}' row; "
                    + $"'{plugin.PluginKey}' is '{plugin.Kind}' and never walks the registration ladder.",
                ],
                PluginConstants.ErrorCodes.InvalidCatalogUpdate);
        }

        plugin.OAuthAuthorizationEndpoint = null;
        plugin.OAuthTokenEndpoint = null;
        plugin.OAuthRevokeEndpoint = null;
        plugin.OAuthRegistrationEndpoint = null;

        // These three are discovery output too - what the authorization server said about CIMD,
        // RFC 9207 and token-endpoint auth. Leaving them behind while clearing the endpoints would
        // have the next connect negotiate against a server it has not actually re-read.
        plugin.OAuthCimdSupported = null;
        plugin.OAuthIssParameterSupported = null;
        plugin.OAuthTokenEndpointAuthMethod = null;

        // A pre-registered client is an operator's own registration and survives: it did not come
        // from discovery, and throwing it away would turn "re-read the server" into "go and
        // register the app again". A cimd or dcr client did come from discovery - it identifies us
        // to endpoints that are now cleared - so it goes with them, and the ladder starts over.
        if (plugin.OAuthClientSource is PluginConstants.OAuthClientSource.Cimd or PluginConstants.OAuthClientSource.Dcr)
        {
            plugin.OAuthClientId = null;
            plugin.OAuthClientSecretEncrypted = null;
            plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved;
        }

        await StampAndSaveAsync(plugin, adminUserId, ct);
        return Result.Success(await ToDetailAsync(plugin, ct));
    }

    public async Task<Result<PluginCatalogDeleteResultDto>> DeleteAsync(
        string pluginKey,
        bool hard,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginCatalogDeleteResultDto>(pluginKey);

        var installationCount = await _unitOfWork.PluginInstallationRepository.CountForPluginAsync(plugin.Id, ct);
        var connectionCount = await _unitOfWork.PluginConnectionRepository.CountForPluginAsync(plugin.Id, ct);

        // Counted for both branches, not only for the delete. plugin_tool_audits and
        // plugin_confirmation_tokens reference plugins(id) ON DELETE CASCADE, so unlike the two
        // counts above they do not stand in a hard delete's way - they go into it, in the same
        // statement, with no error and nothing in the response to say so.
        var auditCount = await _unitOfWork.PluginToolAuditRepository.CountForPluginAsync(plugin.Id, ct);
        var confirmationTokenCount = await _unitOfWork.PluginConfirmationTokenRepository.CountForPluginAsync(plugin.Id, ct);

        if (!hard)
        {
            plugin.IsActive = false;
            await StampAndSaveAsync(plugin, adminUserId, ct);
            return Result.Success(new PluginCatalogDeleteResultDto(
                plugin.PluginKey, false, installationCount, connectionCount, auditCount, confirmationTokenCount));
        }

        // plugin_connections_plugin_id_fkey is ON DELETE RESTRICT, so a hard delete against a
        // referenced row throws inside SaveChangesAsync - a 500 with a Postgres constraint name in
        // it. Refusing here says the same thing in terms an operator can act on, and says it
        // before anything is staged. plugin_installations is checked alongside because deleting a
        // row out from under someone's install list is a data-loss decision, not a cleanup.
        if (installationCount > 0 || connectionCount > 0)
        {
            return Result.Failure<PluginCatalogDeleteResultDto>(
                $"'{plugin.PluginKey}' cannot be hard-deleted: {installationCount} installation(s) and "
                + $"{connectionCount} connection(s) still reference it. Retire it instead - a soft delete "
                + "hides it from every catalog while leaving those references intact.",
                PluginConstants.ErrorCodes.PluginInUse);
        }

        // The guard above is not enough, and the case it misses is the ordinary one: everybody
        // uninstalls a plugin before it is retired, which zeroes the installations and connections
        // and leaves every audit row exactly where it was. Passing the check and then cascading
        // away months of plugin_tool_audits is not a cleanup - those are the compliance records
        // that the workspace plugin-usage endpoint added by this same ticket exists to serve, and
        // they are the record of what the plugin did, which is precisely what is worth keeping
        // about a plugin nobody uses any more.
        //
        // So audits refuse the delete, as installations and connections do. The retirement path is
        // unaffected and is the right answer here anyway: is_active = false removes the row from
        // every catalog, and the FKs - and therefore the history - stay intact. A row that has
        // never been used still hard-deletes, which is the case a hard delete is actually for:
        // undoing a typo in a freshly created MCP row.
        //
        // Confirmation tokens deliberately do not gate. One lives five minutes and is useful only
        // to the single user mid-call on the plugin being deleted, so cascading them is not data
        // loss; the count is carried into the response rather than into this refusal.
        if (auditCount > 0)
        {
            return Result.Failure<PluginCatalogDeleteResultDto>(
                $"'{plugin.PluginKey}' cannot be hard-deleted: {auditCount} tool audit record(s) reference it, "
                + "and plugin_tool_audits cascades - deleting the row would destroy them with no way back. "
                + "Retire it instead - a soft delete hides it from every catalog and keeps its history readable.",
                PluginConstants.ErrorCodes.PluginInUse);
        }

        _unitOfWork.PluginRepository.Remove(plugin);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(new PluginCatalogDeleteResultDto(
            plugin.PluginKey, true, 0, 0, 0, confirmationTokenCount));
    }

    public async Task<Result<PluginToolAuditPageDto>> ListAuditsAsync(
        string pluginKey,
        PluginToolAuditQueryDto query,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginToolAuditPageDto>(pluginKey);

        // Normalised rather than rejected: a paging parameter out of range is a caller bug worth
        // absorbing, and an unbounded pageSize is a denial-of-service knob on a table that has no
        // retention policy. Done here rather than in the controller so there is one answer to
        // "what does pageSize=0 mean" instead of two that can drift apart.
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1
            ? DefaultAuditPageSize
            : Math.Min(query.PageSize, MaxAuditPageSize);
        var outcome = string.IsNullOrWhiteSpace(query.Outcome) ? null : query.Outcome.Trim();

        var (audits, totalCount) = await _unitOfWork.PluginToolAuditRepository.ListForPluginAsync(
            plugin.Id, query.UserId, outcome, (page - 1) * pageSize, pageSize, ct);

        var items = audits
            .Select(audit => new PluginToolAuditEntryDto(
                audit.Id,
                audit.WorkspaceId,
                audit.UserId,
                audit.ConversationId,
                audit.AssistantMessageId,
                audit.PluginKey,
                audit.ToolName,
                audit.InputSummary,
                audit.ResultStatus,
                audit.ProviderResourceRef,
                audit.CreatedAt))
            .ToList();

        return Result.Success(new PluginToolAuditPageDto(items, page, pageSize, totalCount));
    }

    private Task<Plugin?> FindAsync(string pluginKey, CancellationToken ct)
    {
        var key = pluginKey?.Trim() ?? string.Empty;
        // No IsActive filter: a retired row is precisely what an operator comes here to inspect or
        // reinstate.
        return _unitOfWork.PluginRepository.FirstOrDefaultAsync(plugin => plugin.PluginKey == key, ct: ct);
    }

    private async Task StampAndSaveAsync(Plugin plugin, Guid adminUserId, CancellationToken ct)
    {
        // Guid.Empty means the token carried no usable subject. Recording it would assert an
        // attribution that is not true, so the column stays null - which already means "not a
        // person" for every row a migration wrote.
        plugin.UpdatedBy = adminUserId == Guid.Empty ? null : adminUserId;
        plugin.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.PluginRepository.Update(plugin);
        await _unitOfWork.SaveChangesAsync(ct);
    }

    private async Task<PluginCatalogAdminDetailDto> ToDetailAsync(Plugin plugin, CancellationToken ct)
    {
        var installationCount = await _unitOfWork.PluginInstallationRepository.CountForPluginAsync(plugin.Id, ct);
        var connectionCount = await _unitOfWork.PluginConnectionRepository.CountForPluginAsync(plugin.Id, ct);
        var hasSecret = !string.IsNullOrWhiteSpace(plugin.OAuthClientSecretEncrypted);

        return new PluginCatalogAdminDetailDto(
            plugin.PluginKey,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            plugin.Kind,
            plugin.Provider,
            plugin.McpServerUrl,
            PluginScopeMapper.FromJson(plugin.RequiredScopesJson),
            plugin.IsActive,
            plugin.IsFeatured,
            plugin.SortOrder,
            plugin.Category,
            plugin.OAuthClientSource,
            plugin.OAuthClientId,
            !string.IsNullOrWhiteSpace(plugin.OAuthClientId),
            hasSecret,
            // Only meaningful when a secret exists; null otherwise, so a UI is not tempted to
            // render "secret last set" for a row that has never had one.
            hasSecret ? plugin.UpdatedAt : null,
            plugin.OAuthAuthorizationEndpoint,
            plugin.OAuthTokenEndpoint,
            plugin.OAuthRevokeEndpoint,
            plugin.OAuthRegistrationEndpoint,
            plugin.OAuthTokenEndpointAuthMethod,
            plugin.ToolsSyncedAt,
            ReadTools(plugin),
            installationCount,
            connectionCount,
            plugin.UpdatedBy,
            plugin.CreatedAt,
            plugin.UpdatedAt);
    }

    /// <summary>
    /// Deserialises <c>tools_json</c>, treating a manifest that will not parse as empty.
    /// </summary>
    /// <remarks>
    /// The admin surface is where someone goes to fix a row whose manifest is broken. Throwing on
    /// the row would take out the whole listing and leave no way in - so a bad manifest reads as
    /// zero tools, and <c>PUT .../tools</c> overwrites it.
    /// </remarks>
    private static IReadOnlyList<McpToolDescriptorDto> ReadTools(Plugin plugin)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<McpToolDescriptorDto>>(
                       plugin.ToolsJson, PluginDefinitionMapper.JsonOptions)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static Result<T> UnknownPlugin<T>(string pluginKey) =>
        Result.Failure<T>($"No plugin is keyed '{pluginKey}'.", PluginConstants.ErrorCodes.UnknownPlugin);

    private static Result<T> Invalid<T>(IReadOnlyList<string> errors, string errorCode) =>
        Result.Failure<T>(string.Join(" ", errors), errorCode);
}

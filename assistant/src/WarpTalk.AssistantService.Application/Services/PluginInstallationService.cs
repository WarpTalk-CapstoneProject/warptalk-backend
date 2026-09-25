using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginInstallationService : IPluginInstallationService
{
    private readonly IUnitOfWork _unitOfWork;

    private readonly IPluginCredentialProtector _credentialProtector;

    private readonly IWorkspacePluginGuard _workspacePluginGuard;

    private readonly IAdminAuditRecorder _auditRecorder;

    public PluginInstallationService(
        IUnitOfWork unitOfWork,
        IPluginCredentialProtector credentialProtector,
        IWorkspacePluginGuard workspacePluginGuard,
        IAdminAuditRecorder auditRecorder)
    {
        _unitOfWork = unitOfWork;
        _credentialProtector = credentialProtector;
        _workspacePluginGuard = workspacePluginGuard;
        _auditRecorder = auditRecorder;
    }

    public async Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        // Null when the caller names no workspace: no workspace verdict at all, exactly the
        // behaviour that predates WT-646. Otherwise one read of what the workspace has, for an active
        // member of it - the list says which plugins a workspace has, and that is only a member's to
        // read. A non-member gets every marketplace row refused with the same sentence.
        WorkspacePluginAvailability? availability = null;
        string? workspaceRefusal = null;
        if (workspaceId.HasValue)
        {
            var resolved = await _workspacePluginGuard.GetAvailabilityForMemberAsync(workspaceId, userId, ct);
            if (resolved.IsSuccess) availability = resolved.Value;
            else workspaceRefusal = resolved.Error;
        }

        var plugins = await _unitOfWork.PluginRepository.FindAsync(p => p.IsActive, ct: ct);
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(i => i.UserId == userId, ct: ct);
        var connections = await _unitOfWork.PluginConnectionRepository.FindAsync(c => c.UserId == userId, ct: ct);

        var pendingPluginIds = new HashSet<Guid>();
        if (availability is not null)
        {
            var pending = await _unitOfWork.PluginRequestRepository.FindAsync(
                r => r.WorkspaceId == availability.WorkspaceId
                    && r.RequestedBy == userId
                    && r.Status == WorkspacePluginConstants.RequestStatus.Pending,
                ct: ct);
            pendingPluginIds.UnionWith(pending.Select(r => r.PluginId));
        }

        var items = plugins
            .Where(plugin => IsListed(plugin, availability, installations))
            .Select(plugin =>
            {
                var definition = PluginDefinitionMapper.ToDefinition(plugin);
                // Installation is per-plugin; connection is per-provider. The two tiles for Drive
                // and Calendar are installed independently but share one Google grant, so matching
                // the connection on plugin id would leave whichever tile did not happen to start
                // the consent reporting "not connected".
                var installation = installations.FirstOrDefault(i => i.PluginId == plugin.Id);
                var connection = connections.FirstOrDefault(c =>
                    string.Equals(c.Provider, plugin.Provider, StringComparison.Ordinal));

                var workspaceAvailability = availability?.Of(plugin);
                var blockReason = workspaceRefusal
                    ?? (workspaceAvailability is null || WorkspacePluginConstants.Availability.IsUsable(workspaceAvailability)
                        ? null
                        : plugin.OwnerWorkspaceId is not null
                            ? WorkspacePluginConstants.Messages.PrivatePluginNeedsItsWorkspace
                            : workspaceAvailability == WorkspacePluginConstants.Availability.DisabledByPlatform
                                // Says the connection is kept: the row is here only so it can be
                                // seen and, if the member wants, disconnected.
                                ? PluginWorkspaceAccessConstants.Messages.DisabledByPlatform
                                : WorkspacePluginConstants.Messages.NotAdded);

                // The Owner browsing their own member page: a marketplace plugin their workspace has
                // not added is theirs to add, not something to ask themselves for (gap 9).
                var canAdd = availability is { CallerIsOwner: true }
                    && plugin.OwnerWorkspaceId is null
                    && workspaceAvailability == WorkspacePluginConstants.Availability.NotAdded;

                // Reported, not filtered out. A user whose workspace does not have a plugin they have
                // already installed and connected has to be able to see that row to disconnect it;
                // dropping it from the catalog would leave them holding an OAuth grant with no way to
                // revoke it from this product.
                return PluginCatalogItemMapper.ToCatalogItem(
                    definition,
                    installation,
                    connection,
                    blockReason,
                    workspaceAvailability,
                    pendingPluginIds.Contains(plugin.Id) ? WorkspacePluginConstants.RequestStatus.Pending : null,
                    canAdd);
            })
            .ToList();

        return Result.Success<IReadOnlyList<PluginCatalogItemDto>>(items);
    }

    /// <summary>
    /// Every marketplace row the platform lets this workspace have; a private row only inside the
    /// workspace that owns it.
    /// </summary>
    /// <remarks>
    /// The one exception is a private plugin the caller has installed, which stays listed wherever
    /// they look - refused, but there - for the same reason a refused marketplace row stays: it is
    /// the only place its grant can be revoked. That reveals nothing the caller does not already
    /// hold.
    /// </remarks>
    private static bool IsListed(
        Plugin plugin,
        WorkspacePluginAvailability? availability,
        IReadOnlyList<PluginInstallation> installations)
    {
        if (plugin.OwnerWorkspaceId is null)
        {
            // Turned off for this workspace by the platform: hidden, like another workspace's
            // private plugin - except from a member who already installed it, who has to be able
            // to see that it is off here and to disconnect it.
            return availability is null
                || availability.Of(plugin) != WorkspacePluginConstants.Availability.DisabledByPlatform
                || installations.Any(i => i.PluginId == plugin.Id);
        }

        if (availability is not null && plugin.OwnerWorkspaceId == availability.WorkspaceId) return true;
        return installations.Any(i => i.PluginId == plugin.Id);
    }

    public async Task<Result<PluginCatalogItemDto>> InstallAsync(
        string pluginKey,
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginCatalogItemDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // After the catalog lookup so an unknown key still reads as unknown rather than as
        // forbidden, and before anything is written.
        var permitted = await _workspacePluginGuard.CanUsePluginAsync(workspaceId, userId, plugin, ct);
        if (!permitted.IsSuccess)
            return Result.Failure<PluginCatalogItemDto>(permitted.Error!, permitted.ErrorCode);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId && i.PluginId == plugin.Id, ct: ct);

        if (installation == null)
        {
            installation = new PluginInstallation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PluginId = plugin.Id,
                Status = PluginConstants.InstallationStatus.Installed,
                ConfigJson = JsonSerializer.Serialize(new { installedFrom = "assistant_plugins" }),
                InstalledAt = DateTime.UtcNow,
            };
            await _unitOfWork.PluginInstallationRepository.AddAsync(installation, ct);
        }
        else
        {
            installation.Status = PluginConstants.InstallationStatus.Installed;
            installation.DisabledAt = null;
            _unitOfWork.PluginInstallationRepository.Update(installation);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // By provider, the same way ListCatalogAsync reads it. Passing null here hardcoded
        // "not_connected" into the response, so installing Calendar while Drive was already
        // connected answered with a row that said Connect - and a client patching its tile from
        // this response rather than refetching the catalog sent the user through a second Google
        // consent for a grant they already held. That second trip is not merely wasted: consent
        // replaces the shared grant's scopes, so it is also where Drive's access can be dropped.
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider,
            ct: ct);

        return Result.Success(
            PluginCatalogItemMapper.ToCatalogItem(
                PluginDefinitionMapper.ToDefinition(plugin),
                installation,
                connection));
    }

    public async Task<Result> DisableAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId && i.PluginId == plugin.Id, ct: ct);

        if (installation == null)
            return Result.Failure("Plugin is not installed.", PluginConstants.ErrorCodes.PluginNotInstalled);

        installation.Status = PluginConstants.InstallationStatus.Disabled;
        installation.DisabledAt = DateTime.UtcNow;
        // A removed plugin is not connected, and installing it again is not a choice to reconnect
        // it. Left set, a reinstall would come back already connected, and the last-plugin check
        // in DisconnectAsync would count it as still riding on the provider's grant.
        installation.ConnectedAt = null;
        _unitOfWork.PluginInstallationRepository.Update(installation);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<PluginCatalogItemDto>> UpdateToolPolicyAsync(
        string pluginKey,
        Guid userId,
        IReadOnlyDictionary<string, string> tools,
        CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginCatalogItemDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var invalid = tools.FirstOrDefault(entry => !PluginConstants.ToolPolicy.IsKnown(entry.Value));
        if (invalid.Key != null)
            return Result.Failure<PluginCatalogItemDto>(
                $"'{invalid.Value}' is not a tool setting. Use allow, approval or blocked.",
                PluginConstants.ErrorCodes.InvalidToolPolicy);

        // The caller's own installation only. A choice is about what WarpBot does for this user, so
        // there is no row of anyone else's this could reach.
        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        if (installation == null)
            return Result.Failure<PluginCatalogItemDto>("Plugin is not installed.", PluginConstants.ErrorCodes.PluginNotInstalled);

        var definition = PluginDefinitionMapper.ToDefinition(plugin);
        var toolNames = definition.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        // A name the plugin does not have is dropped rather than refused: tools/list can lose a tool
        // between the page loading and the user saving, and that is no reason to lose the rest.
        var known = tools
            .Where(entry => toolNames.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        if (known.Count > 0)
        {
            installation.ConfigJson = PluginToolPolicyStore.Write(installation.ConfigJson, known);
            _unitOfWork.PluginInstallationRepository.Update(installation);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider,
            ct: ct);

        return Result.Success(PluginCatalogItemMapper.ToCatalogItem(definition, installation, connection));
    }

    public async Task<Result<PluginCatalogItemDto>> CreateMcpPluginAsync(
        CreateMcpPluginRequest request,
        Guid userId,
        CancellationToken ct = default)
    {
        var key = request.PluginKey?.Trim() ?? string.Empty;

        // The key, reserved-route, https and provider-collision checks live in McpPluginRows, shared
        // with a workspace Owner's private plugin so the two cannot drift apart.
        var valid = await McpPluginRows.ValidateNewAsync(
            _unitOfWork, key, request.McpServerUrl, PluginConstants.ErrorCodes.UnknownPlugin, ct);
        if (!valid.IsSuccess)
            return Result.Failure<PluginCatalogItemDto>(valid.Error!, valid.ErrorCode);

        var authMode = string.IsNullOrWhiteSpace(request.AuthMode)
            ? PluginConstants.AuthMode.OAuth
            : request.AuthMode.Trim();
        if (!PluginConstants.AuthMode.IsKnown(authMode))
            return Result.Failure<PluginCatalogItemDto>(
                $"'{authMode}' is not a way to connect. Use oauth or api_key.",
                PluginConstants.ErrorCodes.InvalidCatalogUpdate);

        // An API-key row never sends an OAuth client, and the database refuses to store one on it.
        if (authMode == PluginConstants.AuthMode.ApiKey && !string.IsNullOrWhiteSpace(request.OAuth?.ClientId))
            return Result.Failure<PluginCatalogItemDto>(
                "A plugin that connects with an API key cannot also have an OAuth client.",
                PluginConstants.ErrorCodes.InvalidCatalogUpdate);

        // A marketplace row: no owning workspace. Every workspace Owner can add it from there.
        var plugin = McpPluginRows.Build(
            key,
            request.Label,
            request.Description,
            request.McpServerUrl,
            request.AvatarUrl,
            request.RequiredScopes,
            ownerWorkspaceId: null,
            createdBy: userId == Guid.Empty ? null : userId,
            DateTime.UtcNow);

        // Each user pastes their own key; the row holds no client and never walks the ladder.
        if (authMode == PluginConstants.AuthMode.ApiKey)
            plugin.OAuthClientSource = PluginConstants.OAuthClientSource.ApiKey;

        if (request.OAuth is { } oauth && !string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Preregistered;
            plugin.OAuthClientId = oauth.ClientId;
            plugin.OAuthAuthorizationEndpoint = oauth.AuthorizationEndpoint;
            plugin.OAuthTokenEndpoint = oauth.TokenEndpoint;
            plugin.OAuthRevokeEndpoint = oauth.RevokeEndpoint;

            if (!string.IsNullOrWhiteSpace(oauth.ClientSecret))
                plugin.OAuthClientSecretEncrypted = _credentialProtector.Protect(oauth.ClientSecret);
        }

        // Recorded in the platform audit log before it is committed, and not created if it cannot
        // be: a marketplace row reaches every workspace Owner the moment it exists.
        var recorded = await _auditRecorder.RecordPluginActionAsync(
            AdminAuditPluginActions.Created, plugin.Id, userId, beforeSummary: null, PluginAuditSummary.Of(plugin), ct);
        if (!recorded.IsSuccess) return Result.Failure<PluginCatalogItemDto>(recorded.Error!, recorded.ErrorCode);

        await _unitOfWork.PluginRepository.AddAsync(plugin, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Through the mapper rather than constructed here: a freshly created row is not installed
        // and not connected, which is exactly what ToCatalogItem produces from two nulls. Building
        // the DTO by hand is how this call site silently omitted Provider, IsFeatured, SortOrder
        // and Category when they were added - a second construction site only ever falls behind
        // the first.
        var definition = PluginDefinitionMapper.ToDefinition(plugin);
        return Result.Success(PluginCatalogItemMapper.ToCatalogItem(definition, installation: null, connection: null));
    }
}

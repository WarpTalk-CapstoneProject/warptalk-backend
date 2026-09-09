using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginInstallationService : IPluginInstallationService
{
    private readonly IUnitOfWork _unitOfWork;

    private readonly IPluginCredentialProtector _credentialProtector;

    private readonly IWorkspacePluginGuard _workspacePluginGuard;

    public PluginInstallationService(
        IUnitOfWork unitOfWork,
        IPluginCredentialProtector credentialProtector,
        IWorkspacePluginGuard workspacePluginGuard)
    {
        _unitOfWork = unitOfWork;
        _credentialProtector = credentialProtector;
        _workspacePluginGuard = workspacePluginGuard;
    }

    public async Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        // Null when the caller names no workspace, which is the plugins settings page's own case
        // and leaves every row unblocked - exactly the behaviour that predates WT-646. One check
        // for the whole list: the workspace's answer is the same for every row.
        var permitted = await _workspacePluginGuard.CanUsePluginsAsync(workspaceId, ct);

        var plugins = await _unitOfWork.PluginRepository.FindAsync(p => p.IsActive, ct: ct);
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(i => i.UserId == userId, ct: ct);
        var connections = await _unitOfWork.PluginConnectionRepository.FindAsync(c => c.UserId == userId, ct: ct);

        var items = plugins
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
                // Reported, not filtered out. A user in a workspace that has just turned plugins
                // off under an already-installed, already-connected plugin has to be able to see
                // that row to disconnect it; dropping it from the catalog would leave them holding
                // an OAuth grant with no way to revoke it from this product.
                return PluginCatalogItemMapper.ToCatalogItem(
                    definition,
                    installation,
                    connection,
                    permitted.IsSuccess ? null : permitted.Error);
            })
            .ToList();

        return Result.Success<IReadOnlyList<PluginCatalogItemDto>>(items);
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
        var permitted = await _workspacePluginGuard.CanUsePluginsAsync(workspaceId, ct);
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
        return Result.Success(PluginCatalogItemMapper.ToCatalogItem(PluginDefinitionMapper.ToDefinition(plugin), installation, null));
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
        _unitOfWork.PluginInstallationRepository.Update(installation);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<PluginCatalogItemDto>> CreateMcpPluginAsync(
        CreateMcpPluginRequest request,
        Guid userId,
        CancellationToken ct = default)
    {
        var key = request.PluginKey?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure<PluginCatalogItemDto>("A plugin key is required.", PluginConstants.ErrorCodes.UnknownPlugin);

        // Some keys collide with a literal route segment sitting beside a {pluginKey} route, and
        // ASP.NET gives the literal precedence — so the row is not rejected by routing, it is
        // silently unreachable, which is the worse failure. The list lives in PluginConstants
        // rather than being spelled out here, so a new literal route can be declared reserved in
        // one place. The database rejects these too; catching it here gives a usable message.
        if (PluginConstants.IsReservedPluginKey(key))
            return Result.Failure<PluginCatalogItemDto>(
                $"'{key.Trim()}' is reserved and cannot be a plugin key.",
                PluginConstants.ErrorCodes.UnknownPlugin);

        if (string.IsNullOrWhiteSpace(request.McpServerUrl)
            || !Uri.TryCreate(request.McpServerUrl, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure<PluginCatalogItemDto>(
                "An MCP plugin needs an absolute https:// server URL.",
                PluginConstants.ErrorCodes.UnknownPlugin);
        }

        if (await _unitOfWork.PluginRepository.AnyAsync(p => p.PluginKey == key, ct))
            return Result.Failure<PluginCatalogItemDto>($"A plugin keyed '{key}' already exists.", PluginConstants.ErrorCodes.UnknownPlugin);

        // An MCP row takes its key as its provider (below), and since 20260907101000 the provider
        // is the identity of a user's OAuth grant. So a row keyed 'google' would not just look
        // confusing - it would be handed the existing Google connection, complete with its refresh
        // token, and its tools would run against Google's grant. Colliding with any existing
        // provider is rejected outright.
        if (await _unitOfWork.PluginRepository.AnyAsync(p => p.Provider == key, ct))
            return Result.Failure<PluginCatalogItemDto>(
                $"'{key}' is already in use as a provider by another plugin; an MCP plugin needs a provider of its own.",
                PluginConstants.ErrorCodes.UnknownPlugin);

        var now = DateTime.UtcNow;
        var plugin = new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = key,
            Label = request.Label,
            Description = request.Description,
            AvatarUrl = request.AvatarUrl,
            // Its own provider, taken from its own key. Each MCP server is a separate
            // authorization server with a separate grant, so keying connections by provider never
            // merges two MCP plugins - the guard above is what keeps that true.
            Provider = key,
            RequiredScopesJson = JsonSerializer.Serialize(request.RequiredScopes ?? Array.Empty<string>()),
            // Empty until the first connect: for an MCP row this column is a cache of tools/list,
            // not something we author.
            ToolsJson = "[]",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = request.McpServerUrl,
            OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

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

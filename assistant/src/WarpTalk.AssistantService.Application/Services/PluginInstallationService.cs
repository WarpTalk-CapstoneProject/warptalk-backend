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

    public PluginInstallationService(IUnitOfWork unitOfWork, IPluginCredentialProtector credentialProtector)
    {
        _unitOfWork = unitOfWork;
        _credentialProtector = credentialProtector;
    }

    public async Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(Guid userId, CancellationToken ct = default)
    {
        var plugins = await _unitOfWork.PluginRepository.FindAsync(p => p.IsActive, ct: ct);
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(i => i.UserId == userId, ct: ct);
        var connections = await _unitOfWork.PluginConnectionRepository.FindAsync(c => c.UserId == userId, ct: ct);

        var items = plugins
            .Select(plugin =>
            {
                var definition = PluginDefinitionMapper.ToDefinition(plugin);
                var installation = installations.FirstOrDefault(i => i.PluginId == plugin.Id);
                var connection = connections.FirstOrDefault(c => c.PluginId == plugin.Id);
                return PluginCatalogItemMapper.ToCatalogItem(definition, installation, connection);
            })
            .ToList();

        return Result.Success<IReadOnlyList<PluginCatalogItemDto>>(items);
    }

    public async Task<Result<PluginCatalogItemDto>> InstallAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginCatalogItemDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

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

        // 'mcp' is the literal segment of the shared OAuth callback route, and ASP.NET gives it
        // precedence over {pluginKey}. A row keyed 'mcp' would shadow the callback for every MCP
        // plugin at once. The database rejects it too; catching it here gives a usable message.
        if (string.Equals(key, PluginConstants.PluginKind.Mcp, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<PluginCatalogItemDto>("'mcp' is reserved and cannot be a plugin key.", PluginConstants.ErrorCodes.UnknownPlugin);

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

        var now = DateTime.UtcNow;
        var plugin = new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = key,
            Label = request.Label,
            Description = request.Description,
            AvatarUrl = request.AvatarUrl,
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

        var definition = PluginDefinitionMapper.ToDefinition(plugin);
        return Result.Success(new PluginCatalogItemDto(
            plugin.PluginKey,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            PluginScopeMapper.FromJson(plugin.RequiredScopesJson),
            PluginConstants.InstallationStatus.NotInstalled,
            PluginConstants.ConnectionStatus.NotConnected,
            null,
            definition.Tools,
            Array.Empty<string>()));
    }
}

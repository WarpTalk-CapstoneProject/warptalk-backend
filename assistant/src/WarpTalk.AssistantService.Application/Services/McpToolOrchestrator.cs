using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class McpToolOrchestrator : IMcpToolOrchestrator
{
    /// <summary>
    /// Refresh this far ahead of the recorded expiry so a token that dies in flight does not cost
    /// the user a round trip.
    /// </summary>
    private static readonly TimeSpan AccessTokenExpirySkew = TimeSpan.FromSeconds(60);

    private readonly IMcpToolGateway _gateway;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspacePluginPolicyClient _workspacePluginPolicyClient;
    private readonly IPluginTokenRefresher _tokenRefresher;

    public McpToolOrchestrator(
        IMcpToolGateway gateway,
        IUnitOfWork unitOfWork,
        IWorkspacePluginPolicyClient workspacePluginPolicyClient,
        IPluginTokenRefresher tokenRefresher)
    {
        _gateway = gateway;
        _unitOfWork = unitOfWork;
        _workspacePluginPolicyClient = workspacePluginPolicyClient;
        _tokenRefresher = tokenRefresher;
    }

    public async Task<Result<IReadOnlyList<McpToolDescriptorDto>>> ListAvailableToolsAsync(Guid userId, Guid? workspaceId, CancellationToken ct = default)
    {
        if (workspaceId.HasValue && !await _workspacePluginPolicyClient.AllowsPluginUsageAsync(workspaceId.Value, ct))
            return Result.Success<IReadOnlyList<McpToolDescriptorDto>>(Array.Empty<McpToolDescriptorDto>());

        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            i => i.UserId == userId && i.Status == PluginConstants.InstallationStatus.Installed, ct: ct);
        var installedPluginIds = installations.Select(i => i.PluginId).ToHashSet();

        var plugins = await _unitOfWork.PluginRepository.FindAsync(
            p => installedPluginIds.Contains(p.Id) && p.IsActive, ct: ct);

        var tools = plugins
            .Select(PluginDefinitionMapper.ToDefinition)
            .SelectMany(plugin => plugin.Tools)
            .ToList();

        return Result.Success<IReadOnlyList<McpToolDescriptorDto>>(tools);
    }

    public async Task<Result<McpToolExecutionResult>> ExecuteAsync(Guid userId, McpToolExecutionRequest request, CancellationToken ct = default)
    {
        var pluginEntity = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == request.PluginKey && p.IsActive, ct: ct);
        if (pluginEntity == null)
            return Result.Failure<McpToolExecutionResult>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var plugin = PluginDefinitionMapper.ToDefinition(pluginEntity);
        var tool = plugin.Tools.FirstOrDefault(t => string.Equals(t.Name, request.ToolName, StringComparison.Ordinal));
        if (tool == null)
            return Result.Failure<McpToolExecutionResult>("Unknown MCP tool.", PluginConstants.ErrorCodes.UnknownTool);

        if (request.WorkspaceId.HasValue && !await _workspacePluginPolicyClient.AllowsPluginUsageAsync(request.WorkspaceId.Value, ct))
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                PluginConstants.ErrorCodes.PermissionDenied,
                "Workspace settings do not allow personal plugins in WarpBot.",
                ct);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);

        if (installation == null)
            return await McpToolAuditRecorder.RecordFailureAsync(_unitOfWork, userId, plugin.Id, request, PluginConstants.ErrorCodes.PluginNotInstalled, "Plugin is not installed.", ct);

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId
                && c.PluginId == plugin.Id
                && c.Status == PluginConstants.ConnectionStatus.Connected,
            ct: ct);

        if (connection == null)
            return await McpToolAuditRecorder.RecordFailureAsync(_unitOfWork, userId, plugin.Id, request, PluginConstants.ErrorCodes.ConnectionRequired, "Connect your provider account first.", ct);

        var grantedScopes = PluginScopeMapper.FromJson(connection.ScopesJson).ToHashSet(StringComparer.Ordinal);
        var missingScopes = tool.RequiredScopes.Where(scope => !grantedScopes.Contains(scope)).ToList();
        if (missingScopes.Count > 0)
            return await McpToolAuditRecorder.RecordFailureAsync(_unitOfWork, userId, plugin.Id, request, PluginConstants.ErrorCodes.MissingScope, "Reconnect the provider account with the required scopes.", ct);

        if (tool.Effect == PluginConstants.ToolEffect.Write && string.IsNullOrWhiteSpace(request.ConfirmationToken))
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                PluginConstants.ErrorCodes.ConfirmationRequired,
                "Confirm this action before WarpBot changes data in the connected app.",
                ct,
                confirmationToken: McpConfirmationTokenFactory.Create(userId, request));

        if (tool.Effect == PluginConstants.ToolEffect.Write
            && !McpConfirmationTokenFactory.Matches(userId, request, request.ConfirmationToken))
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                PluginConstants.ErrorCodes.PermissionDenied,
                "Confirmation token does not match this plugin action.",
                ct);

        // Refresh decision sits here, after every gate: the gates can end the call without
        // touching the provider (a write tool still awaiting confirmation, for instance), and
        // burning the refresh there would waste the one attempt this execution gets.
        var refreshAttempted = false;
        if (IsAccessTokenExpired(connection))
        {
            refreshAttempted = true;
            var proactiveRefresh = await _tokenRefresher.RefreshAccessTokenAsync(pluginEntity, connection, ct);
            if (!proactiveRefresh.IsSuccess)
                return await McpToolAuditRecorder.RecordFailureAsync(
                    _unitOfWork,
                    userId,
                    plugin.Id,
                    request,
                    PluginConstants.ErrorCodes.ConnectionRequired,
                    "Reconnect your provider account first.",
                    ct);
        }

        var result = await _gateway.ExecuteAsync(plugin, tool, connection, request, ct);

        // The stored expiry can lag reality - clock skew, or a grant revoked in the provider's
        // account UI - so the provider's own 401 is the second and last trigger.
        if (!refreshAttempted
            && !result.IsSuccess
            && string.Equals(result.ErrorCode, PluginConstants.ErrorCodes.ConnectionRequired, StringComparison.Ordinal))
        {
            var reactiveRefresh = await _tokenRefresher.RefreshAccessTokenAsync(pluginEntity, connection, ct);
            result = reactiveRefresh.IsSuccess
                ? await _gateway.ExecuteAsync(plugin, tool, connection, request, ct)
                : McpToolExecutionResultMapper.ToFailure(
                    PluginConstants.ErrorCodes.ConnectionRequired,
                    "Reconnect your provider account first.");
        }

        await McpToolAuditRecorder.RecordAsync(
            _unitOfWork,
            userId,
            plugin.Id,
            request,
            result.IsSuccess ? "success" : result.ErrorCode ?? "failed",
            result.ProviderResourceRef,
            ct);

        return Result.Success(result);
    }

    /// <summary>
    /// <c>access_token_expires_at</c> is a <c>timestamptz</c> written from <see cref="DateTime.UtcNow"/>,
    /// so it compares directly against UTC now. A connection with no recorded expiry is treated as
    /// still valid - the provider 401 path below catches it.
    /// </summary>
    private static bool IsAccessTokenExpired(PluginConnection connection)
    {
        return connection.AccessTokenExpiresAt.HasValue
            && connection.AccessTokenExpiresAt.Value - AccessTokenExpirySkew <= DateTime.UtcNow;
    }
}

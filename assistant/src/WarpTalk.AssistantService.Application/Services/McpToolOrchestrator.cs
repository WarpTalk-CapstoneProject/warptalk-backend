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
    private readonly IPluginProviderResolver _providerResolver;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspacePluginGuard _workspacePluginGuard;
    private readonly IPluginTokenRefresher _tokenRefresher;
    private readonly IMcpConfirmationTokenService _confirmationTokenService;

    public McpToolOrchestrator(
        IPluginProviderResolver providerResolver,
        IUnitOfWork unitOfWork,
        IWorkspacePluginGuard workspacePluginGuard,
        IPluginTokenRefresher tokenRefresher,
        IMcpConfirmationTokenService confirmationTokenService)
    {
        _providerResolver = providerResolver;
        _unitOfWork = unitOfWork;
        _workspacePluginGuard = workspacePluginGuard;
        _tokenRefresher = tokenRefresher;
        _confirmationTokenService = confirmationTokenService;
    }

    public async Task<Result<IReadOnlyList<McpToolDescriptorDto>>> ListAvailableToolsAsync(
        Guid userId,
        Guid? workspaceId,
        IReadOnlyCollection<string>? excludedPluginKeys = null,
        CancellationToken ct = default)
    {
        // What this workspace has, read once for the whole list.
        //
        // An empty list, not a refusal, when the caller is not a member or named no workspace. This
        // is what WarpBot may reach for in this conversation, so a plugin the workspace does not
        // have never reaches the model and it never proposes an action that would be refused.
        //
        // The in-workspace check, so an omitted or borrowed workspaceId cannot widen the list: this
        // is a conversation, and a conversation has a workspace.
        var availability = await _workspacePluginGuard.GetAvailabilityForMemberAsync(workspaceId, userId, ct);
        if (!availability.IsSuccess)
            return Result.Success<IReadOnlyList<McpToolDescriptorDto>>(Array.Empty<McpToolDescriptorDto>());

        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            i => i.UserId == userId && i.Status == PluginConstants.InstallationStatus.Installed, ct: ct);
        var installationsByPlugin = installations
            .GroupBy(i => i.PluginId)
            .ToDictionary(group => group.Key, group => group.First());
        var installedPluginIds = installationsByPlugin.Keys.ToHashSet();

        var plugins = await _unitOfWork.PluginRepository.FindAsync(
            p => installedPluginIds.Contains(p.Id) && p.IsActive, ct: ct);

        // WT-687. Two things the user decided narrow the list before the model sees it:
        //
        //   - A plugin switched off for this conversation. A preference, not a security boundary -
        //     the caller composes the list - which is why it only ever removes tools here and is not
        //     consulted by ExecuteAsync.
        //   - A tool the user blocked. Left out so the model never plans around it; ExecuteAsync
        //     refuses it as well, because that one IS a boundary.
        var excluded = excludedPluginKeys?.ToHashSet(StringComparer.Ordinal) ?? [];

        var tools = plugins
            // A plugin the user installed is still only offered where the workspace has it.
            .Where(availability.Value!.IsUsable)
            .Where(plugin => !excluded.Contains(plugin.PluginKey))
            .SelectMany(plugin => PluginToolPolicyStore.WithPolicies(
                PluginDefinitionMapper.ToDefinition(plugin).Tools,
                installationsByPlugin[plugin.Id].ConfigJson))
            .Where(tool => tool.Policy != PluginConstants.ToolPolicy.Blocked)
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

        // The last gate a plugin in a workspace that has turned plugins off has to pass.
        // Installation and connection rows are deliberately left alone when an admin tightens the
        // policy - see PluginInstallationService.ListCatalogAsync - so this is what actually stops
        // the tool running, and it is checked on every call rather than at install time because
        // the policy can change between the two.
        //
        // WorkspaceId is a field of a request the caller composes, so this asks the in-workspace
        // question: absent is a refusal, not an absence, and the caller has to belong to the
        // workspace they named. Omitting the field used to pass this gate outright, and the audit
        // row it wrote carried workspace_id = NULL - so the calls that slipped past the policy were
        // also the ones its Owner could not see.
        var policyCheck = await _workspacePluginGuard.CanUsePluginInWorkspaceAsync(
            request.WorkspaceId,
            userId,
            pluginEntity,
            ct);
        if (!policyCheck.IsSuccess)
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                policyCheck.ErrorCode!,
                policyCheck.Error!,
                ct);

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);

        if (installation == null)
            return await McpToolAuditRecorder.RecordFailureAsync(_unitOfWork, userId, plugin.Id, request, PluginConstants.ErrorCodes.PluginNotInstalled, "Plugin is not installed.", ct);

        // Installed but never connected. The provider's grant may well cover this tool - Meet rides
        // on the scope Calendar was granted - but the user did not connect this plugin, so WarpBot
        // does not get to act through it.
        if (installation.ConnectedAt is null)
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                BuildConnectionRequiredResult(plugin, connection: null),
                ct);

        // WT-687. The user's own choice for this tool. Blocked is refused here even though the list
        // never offers it: a model working from an earlier turn's tool list, or a caller that skips
        // the list, must not get through on a tool the user switched off.
        var policy = PluginToolPolicyStore.Resolve(tool, PluginToolPolicyStore.Read(installation.ConfigJson));
        if (policy == PluginConstants.ToolPolicy.Blocked)
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                PluginConstants.ErrorCodes.ToolBlocked,
                $"{tool.Label} is blocked for WarpBot. It can be allowed again from the {plugin.Label} plugin settings.",
                ct);

        // The grant is the provider's, not the plugin's. The per-tool scope check below is what
        // still separates the products: a Drive tool needs drive.readonly on this connection
        // whether the user consented through the Drive tile or the Calendar one.
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId
                && c.Provider == plugin.Provider,
            ct: ct);

        if (connection == null || connection.Status != PluginConstants.ConnectionStatus.Connected)
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                BuildConnectionRequiredResult(plugin, connection),
                ct);

        var grantedScopes = PluginScopeMapper.FromJson(connection.ScopesJson).ToHashSet(StringComparer.Ordinal);
        var missingScopes = tool.RequiredScopes.Where(scope => !grantedScopes.Contains(scope)).ToList();
        if (missingScopes.Count > 0)
            return await McpToolAuditRecorder.RecordFailureAsync(_unitOfWork, userId, plugin.Id, request, PluginConstants.ErrorCodes.MissingScope, "Reconnect the provider account with the required scopes.", ct);

        // Asks when the user's policy says to, not when the tool writes. Before WT-687 the two were
        // the same thing; now a user can trust a write tool (allow) or want to see a read tool
        // coming (approval), and the policy already defaults to the old rule when they have not.
        if (policy == PluginConstants.ToolPolicy.Approval && string.IsNullOrWhiteSpace(request.ConfirmationToken))
        {
            var token = await _confirmationTokenService.CreateAsync(userId, plugin.Id, request, ct);
            return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                PluginConstants.ErrorCodes.ConfirmationRequired,
                "Confirm this action before WarpBot changes data in the connected app.",
                ct,
                confirmationToken: token.Value);
        }

        if (policy == PluginConstants.ToolPolicy.Approval)
        {
            var confirmation = await _confirmationTokenService.ValidateAndConsumeAsync(
                userId,
                plugin.Id,
                request,
                request.ConfirmationToken!,
                ct);

            if (!confirmation.IsSuccess)
            {
                string? freshToken = null;
                if (string.Equals(confirmation.ErrorCode, PluginConstants.ErrorCodes.ConfirmationRequired, StringComparison.Ordinal))
                {
                    var token = await _confirmationTokenService.CreateAsync(userId, plugin.Id, request, ct);
                    freshToken = token.Value;
                }

                return await McpToolAuditRecorder.RecordFailureAsync(
                _unitOfWork,
                userId,
                plugin.Id,
                request,
                    confirmation.ErrorCode ?? PluginConstants.ErrorCodes.PermissionDenied,
                    confirmation.Error ?? "Confirmation token does not match this plugin action.",
                    ct,
                    confirmationToken: freshToken);
            }

            // "Always allow" on the card. Recorded only here, after a token that validated, so the
            // flag cannot turn a tool to allow without the user having been shown a real card for it.
            if (request.AlwaysAllow)
            {
                installation.ConfigJson = PluginToolPolicyStore.Write(
                    installation.ConfigJson,
                    new Dictionary<string, string> { [tool.Name] = PluginConstants.ToolPolicy.Allow });
                _unitOfWork.PluginInstallationRepository.Update(installation);
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        // Refresh decision sits here, after every gate: the gates can end the call without
        // touching the provider (a write tool still awaiting confirmation, for instance), and
        // burning the refresh there would waste the one attempt this execution gets.
        var refreshAttempted = false;
        if (McpToolAccessTokenPolicy.IsExpiredOrExpiring(connection))
        {
            refreshAttempted = true;
            var proactiveRefresh = await _tokenRefresher.RefreshAccessTokenAsync(pluginEntity, connection, ct);
            if (!proactiveRefresh.IsSuccess)
                // The refresher already decided whether the grant is dead (connection_required,
                // row now expired) or the provider was merely unreachable (provider_unavailable /
                // provider_rate_limited, row untouched). Pass its verdict through unflattened so
                // the model tells the user to retry rather than to reconnect, and so the audit row
                // records what actually happened.
                return await McpToolAuditRecorder.RecordFailureAsync(
                    _unitOfWork,
                    userId,
                    plugin.Id,
                    request,
                    string.Equals(proactiveRefresh.ErrorCode, PluginConstants.ErrorCodes.ConnectionRequired, StringComparison.Ordinal)
                        ? BuildConnectionRequiredResult(plugin, connection, proactiveRefresh.Error)
                        : McpToolExecutionResultMapper.ToFailure(
                            proactiveRefresh.ErrorCode ?? PluginConstants.ErrorCodes.ConnectionRequired,
                            proactiveRefresh.Error ?? "Reconnect your provider account first."),
                    ct);
        }

        var gateway = _providerResolver.ResolveGateway(plugin.Kind);
        var result = await gateway.ExecuteAsync(plugin, tool, connection, request, ct);

        // The stored expiry can lag reality - clock skew, or a grant revoked in the provider's
        // account UI - so the provider's own 401 is the second and last trigger.
        if (!refreshAttempted
            && !result.IsSuccess
            && string.Equals(result.ErrorCode, PluginConstants.ErrorCodes.ConnectionRequired, StringComparison.Ordinal))
        {
            var reactiveRefresh = await _tokenRefresher.RefreshAccessTokenAsync(pluginEntity, connection, ct);

            // Worth spelling out, because the obvious move is wrong. The gateway just answered
            // connection_required off a real provider 401, so it is tempting to keep that result
            // when the refresh then fails. But a *transient* refresh failure means we never got an
            // answer about the grant: the access token we hold is stale (that much the 401 proves)
            // and the token endpoint was unreachable, which proves nothing. Telling the user to
            // reconnect there sends them through a browser consent to fix what may be a ten-second
            // outage - and re-consenting is the one action they cannot take back cheaply.
            // So the transient code wins over the gateway's connection_required. If the grant
            // really is dead, the next turn retries, the token endpoint answers invalid_grant, and
            // the user gets connection_required then - one extra turn, and self-correcting.
            // A *permanent* refresh failure returns connection_required anyway, so that path is
            // unchanged.
            result = reactiveRefresh.IsSuccess
                ? await gateway.ExecuteAsync(plugin, tool, connection, request, ct)
                : string.Equals(reactiveRefresh.ErrorCode, PluginConstants.ErrorCodes.ConnectionRequired, StringComparison.Ordinal)
                    ? BuildConnectionRequiredResult(plugin, connection, reactiveRefresh.Error)
                    : McpToolExecutionResultMapper.ToFailure(
                        reactiveRefresh.ErrorCode ?? PluginConstants.ErrorCodes.ConnectionRequired,
                        reactiveRefresh.Error ?? "Reconnect your provider account first.");
        }

        if (!result.IsSuccess
            && string.Equals(result.ErrorCode, PluginConstants.ErrorCodes.ConnectionRequired, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(result.PluginKey))
        {
            result = BuildConnectionRequiredResult(plugin, connection, result.Message);
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

    private static McpToolExecutionResult BuildConnectionRequiredResult(
        PluginDefinitionDto plugin,
        PluginConnection? connection,
        string? message = null)
    {
        var status = connection?.Status;
        if (string.IsNullOrWhiteSpace(status))
            status = PluginConstants.ConnectionStatus.NotConnected;
        else if (status == PluginConstants.ConnectionStatus.Connected)
            status = PluginConstants.ConnectionStatus.Expired;

        return McpToolExecutionResultMapper.ToConnectionRequiredFailure(
            plugin,
            status,
            connection?.ProviderEmail,
            message ?? ConnectionRequiredMessage(plugin.Label, status));
    }

    private static string ConnectionRequiredMessage(string pluginLabel, string connectionStatus)
    {
        return connectionStatus switch
        {
            PluginConstants.ConnectionStatus.Expired =>
                $"Your {pluginLabel} connection has expired. Reconnect it before WarpBot can use it for this request.",
            PluginConstants.ConnectionStatus.Revoked =>
                $"Reconnect {pluginLabel} before WarpBot can use it for this request.",
            _ =>
                $"Connect {pluginLabel} before WarpBot can use it for this request.",
        };
    }

}

using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IWorkspacePluginMarketplaceService"/>
public class WorkspacePluginMarketplaceService : IWorkspacePluginMarketplaceService
{
    private const int MaxKeyAttempts = 5;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspacePluginGuard _guard;
    private readonly IWorkspacePluginPolicyClient _policyClient;
    private readonly IWorkspaceMembershipClient _membershipClient;
    private readonly IWorkspaceDirectoryClient _directoryClient;
    private readonly IUserNotificationClient _notificationClient;
    private readonly ILogger<WorkspacePluginMarketplaceService> _logger;

    public WorkspacePluginMarketplaceService(
        IUnitOfWork unitOfWork,
        IWorkspacePluginGuard guard,
        IWorkspacePluginPolicyClient policyClient,
        IWorkspaceMembershipClient membershipClient,
        IWorkspaceDirectoryClient directoryClient,
        IUserNotificationClient notificationClient,
        ILogger<WorkspacePluginMarketplaceService> logger)
    {
        _unitOfWork = unitOfWork;
        _guard = guard;
        _policyClient = policyClient;
        _membershipClient = membershipClient;
        _directoryClient = directoryClient;
        _notificationClient = notificationClient;
        _logger = logger;
    }

    // ---- reads ----------------------------------------------------------------------------------

    public async Task<Result<WorkspacePluginsOverviewDto>> GetOverviewAsync(
        Guid workspaceId,
        Guid callerId,
        CancellationToken ct = default)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Denied<WorkspacePluginsOverviewDto>(WorkspacePluginConstants.Messages.OwnerOrAdminOnly);

        var availability = await _guard.GetAvailabilityAsync(workspaceId, ct);
        var plugins = await _unitOfWork.PluginRepository.FindAsync(
            p => p.IsActive && (p.OwnerWorkspaceId == null || p.OwnerWorkspaceId == workspaceId),
            ct: ct);
        var rows = await _unitOfWork.WorkspacePluginRepository.FindAsync(row => row.WorkspaceId == workspaceId, ct: ct);
        var rowsByPlugin = rows.ToDictionary(row => row.PluginId);
        var usedCounts = await _unitOfWork.PluginToolAuditRepository.CountDistinctUsersByPluginForWorkspaceAsync(workspaceId, ct);

        WorkspacePluginItemDto ToItem(Plugin plugin)
        {
            rowsByPlugin.TryGetValue(plugin.Id, out var row);
            return ToItemDto(
                plugin,
                availability.Of(plugin),
                row,
                usedCounts.TryGetValue(plugin.Id, out var used) ? used : 0);
        }

        var ordered = plugins
            .OrderBy(plugin => plugin.SortOrder)
            .ThenBy(plugin => plugin.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inWorkspace = ordered.Where(availability.IsUsable).Select(ToItem).ToList();
        var marketplace = ordered
            .Where(plugin => plugin.OwnerWorkspaceId == null && !availability.IsUsable(plugin))
            .Select(ToItem)
            .ToList();

        var pending = await ListRequestsAsync(workspaceId, ct);

        return Result.Success(new WorkspacePluginsOverviewDto(
            workspaceId,
            availability.IsCurated,
            IsOwner(membership),
            inWorkspace,
            marketplace,
            pending));
    }

    public async Task<Result<IReadOnlyList<WorkspacePluginRequestDto>>> ListPendingRequestsAsync(
        Guid workspaceId,
        Guid callerId,
        CancellationToken ct = default)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Denied<IReadOnlyList<WorkspacePluginRequestDto>>(WorkspacePluginConstants.Messages.OwnerOrAdminOnly);

        return Result.Success(await ListRequestsAsync(workspaceId, ct));
    }

    public async Task<Result<IReadOnlyList<WorkspacePluginRequestDto>>> ListMyRequestsAsync(
        Guid workspaceId,
        Guid callerId,
        CancellationToken ct = default)
    {
        if (!await IsActiveMemberAsync(workspaceId, callerId, ct))
            return Denied<IReadOnlyList<WorkspacePluginRequestDto>>(PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember);

        var requests = await _unitOfWork.PluginRequestRepository.FindAsync(
            request => request.WorkspaceId == workspaceId && request.RequestedBy == callerId,
            ct: ct);
        return Result.Success(await ToRequestDtosAsync(requests.OrderByDescending(r => r.CreatedAt).ToList(), ct));
    }

    // ---- the Owner's list -----------------------------------------------------------------------

    public async Task<Result<WorkspacePluginItemDto>> AddMarketplacePluginAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default)
    {
        if (!await IsOwnerAsync(workspaceId, callerId, ct))
            return Denied<WorkspacePluginItemDto>(WorkspacePluginConstants.Messages.OwnerOnly);

        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == pluginKey && p.OwnerWorkspaceId == null,
            ct: ct);
        if (plugin is null) return UnknownPlugin<WorkspacePluginItemDto>();

        var added = await AddToListAsync(workspaceId, callerId, plugin, ct);
        if (!added.IsSuccess) return Result.Failure<WorkspacePluginItemDto>(added.Error!, added.ErrorCode);

        // Anyone who was waiting for exactly this has their answer now: the mock's Add clears the
        // requests for the plugin, and leaving them pending would ask the Owner to decide a
        // question already settled.
        var settled = await SettlePendingRequestsAsync(
            workspaceId, plugin.Id, callerId, WorkspacePluginConstants.RequestStatus.Approved, ct);

        await _unitOfWork.SaveChangesAsync(ct);
        await NotifyDecidedAsync(workspaceId, settled, plugin, ct);

        return Result.Success(ToItemDto(plugin, WorkspacePluginConstants.Availability.Added, added.Value, 0));
    }

    public async Task<Result> RemovePluginAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default)
    {
        if (!await IsOwnerAsync(workspaceId, callerId, ct))
            return Result.Failure(WorkspacePluginConstants.Messages.OwnerOnly, PluginConstants.ErrorCodes.PermissionDenied);

        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == pluginKey && (p.OwnerWorkspaceId == null || p.OwnerWorkspaceId == workspaceId),
            ct: ct);
        if (plugin is null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        if (plugin.OwnerWorkspaceId == workspaceId)
        {
            // Retired, never deleted. Members may have connected it, so it holds OAuth grants and
            // audit history that a hard delete would take with it; is_active=false removes it from
            // every catalog and stops every tool call, which is what "remove" means to the Owner.
            plugin.IsActive = false;
            plugin.UpdatedBy = callerId;
            plugin.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.PluginRepository.Update(plugin);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }

        // Materialised first, so removing one plugin from a workspace still on "every plugin" leaves
        // it with every OTHER plugin rather than with none.
        var curated = await EnsureCuratedAsync(workspaceId, callerId, ct);
        if (!curated.IsSuccess) return Result.Failure(curated.Error!, curated.ErrorCode);

        var row = curated.Value!.FirstOrDefault(r => r.PluginId == plugin.Id)
            ?? await _unitOfWork.WorkspacePluginRepository.FirstOrDefaultAsync(
                r => r.WorkspaceId == workspaceId && r.PluginId == plugin.Id,
                ct: ct);

        // Removing a row that was only just staged by the seed un-stages it, which is the same
        // outcome: the plugin is not on the list that gets written.
        if (row is not null) _unitOfWork.WorkspacePluginRepository.Remove(row);

        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- private plugins ------------------------------------------------------------------------

    public async Task<Result<WorkspacePluginItemDto>> CreatePrivatePluginAsync(
        Guid workspaceId,
        Guid callerId,
        CreatePrivatePluginRequest request,
        CancellationToken ct = default)
    {
        if (!await IsOwnerAsync(workspaceId, callerId, ct))
            return Denied<WorkspacePluginItemDto>(WorkspacePluginConstants.Messages.OwnerOnly);

        var label = request.Label?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        var url = request.McpServerUrl?.Trim();

        var fields = ValidatePrivateFields(label, description);
        if (!fields.IsSuccess) return Result.Failure<WorkspacePluginItemDto>(fields.Error!, fields.ErrorCode);

        var urlCheck = McpPluginRows.ValidatePublicServerUrl(url, WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);
        if (!urlCheck.IsSuccess) return Result.Failure<WorkspacePluginItemDto>(urlCheck.Error!, urlCheck.ErrorCode);

        // The key is derived, and derived with a random suffix, so a collision is astronomically
        // unlikely - but the check that refuses one is the check that keeps a new row from being
        // handed another plugin's OAuth grant, so it still runs, and a collision just rolls again.
        string? key = null;
        for (var attempt = 0; attempt < MaxKeyAttempts && key is null; attempt++)
        {
            var candidate = McpPluginRows.DerivePrivateKey(label);
            var valid = await McpPluginRows.ValidateNewAsync(
                _unitOfWork, candidate, url, WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin, ct);
            if (valid.IsSuccess) key = candidate;
        }

        if (key is null)
            return Result.Failure<WorkspacePluginItemDto>(
                "Could not create a unique key for this plugin. Try again.",
                WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);

        var plugin = McpPluginRows.Build(
            key,
            label,
            description,
            url!,
            avatarUrl: null,
            requiredScopes: null,
            ownerWorkspaceId: workspaceId,
            createdBy: callerId,
            DateTime.UtcNow);

        await _unitOfWork.PluginRepository.AddAsync(plugin, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(ToItemDto(plugin, WorkspacePluginConstants.Availability.Private, row: null, 0));
    }

    public async Task<Result<WorkspacePluginItemDto>> UpdatePrivatePluginAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        UpdatePrivatePluginRequest request,
        CancellationToken ct = default)
    {
        if (!await IsOwnerAsync(workspaceId, callerId, ct))
            return Denied<WorkspacePluginItemDto>(WorkspacePluginConstants.Messages.OwnerOnly);

        // Scoped to this workspace in the query itself: another workspace's private plugin and a
        // marketplace row are both simply "not one of yours", never "forbidden".
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == pluginKey && p.OwnerWorkspaceId == workspaceId && p.IsActive,
            ct: ct);
        if (plugin is null)
            return Result.Failure<WorkspacePluginItemDto>(
                "This workspace has no private plugin with that key.",
                WorkspacePluginConstants.ErrorCodes.NotAPrivatePlugin);

        var label = request.Label is null ? plugin.Label : request.Label.Trim();
        var description = request.Description is null ? plugin.Description : request.Description.Trim();
        var fields = ValidatePrivateFields(label, description);
        if (!fields.IsSuccess) return Result.Failure<WorkspacePluginItemDto>(fields.Error!, fields.ErrorCode);

        if (request.McpServerUrl is not null)
        {
            var url = request.McpServerUrl.Trim();
            if (!string.Equals(url, plugin.McpServerUrl, StringComparison.Ordinal))
            {
                var urlCheck = McpPluginRows.ValidatePublicServerUrl(url, WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);
                if (!urlCheck.IsSuccess) return Result.Failure<WorkspacePluginItemDto>(urlCheck.Error!, urlCheck.ErrorCode);

                // A connection is a grant from THIS server's authorization server. Pointing the row
                // at another server would send members' tokens to a host they never consented to,
                // so the server can only change while nobody is connected.
                var connections = await _unitOfWork.PluginConnectionRepository.CountForPluginAsync(plugin.Id, ct);
                if (connections > 0)
                    return Result.Failure<WorkspacePluginItemDto>(
                        "Members are connected to this plugin's current server. Remove it and add a new plugin to use a different server.",
                        WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);

                plugin.McpServerUrl = url;
                // Everything learnt from the old server belongs to the old server.
                plugin.ToolsJson = "[]";
                plugin.ToolsSyncedAt = null;
                plugin.ToolsManifestHash = null;
                plugin.OAuthAuthorizationEndpoint = null;
                plugin.OAuthTokenEndpoint = null;
                plugin.OAuthRevokeEndpoint = null;
                plugin.OAuthRegistrationEndpoint = null;
                plugin.OAuthClientId = null;
                plugin.OAuthClientSecretEncrypted = null;
                plugin.OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved;
                plugin.OAuthCimdSupported = null;
                plugin.OAuthIssParameterSupported = null;
                plugin.OAuthTokenEndpointAuthMethod = null;
            }
        }

        plugin.Label = label;
        plugin.Description = description;
        plugin.UpdatedBy = callerId;
        plugin.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.PluginRepository.Update(plugin);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(ToItemDto(plugin, WorkspacePluginConstants.Availability.Private, row: null, 0));
    }

    // ---- requests -------------------------------------------------------------------------------

    public async Task<Result<WorkspacePluginRequestDto>> CreateRequestAsync(
        Guid workspaceId,
        Guid callerId,
        string? requesterEmail,
        CreatePluginRequestRequest request,
        CancellationToken ct = default)
    {
        if (!await IsActiveMemberAsync(workspaceId, callerId, ct))
            return Denied<WorkspacePluginRequestDto>(PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember);

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > WorkspacePluginConstants.MaxRequestReasonLength })
            return Result.Failure<WorkspacePluginRequestDto>(
                $"Keep the reason under {WorkspacePluginConstants.MaxRequestReasonLength} characters.",
                WorkspacePluginConstants.ErrorCodes.InvalidRequest);

        // Marketplace rows only. A private plugin is already in its workspace, and another
        // workspace's is not something a member here may learn exists - so both read as unknown.
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == request.PluginKey && p.OwnerWorkspaceId == null && p.IsActive,
            ct: ct);
        if (plugin is null) return UnknownPlugin<WorkspacePluginRequestDto>();

        var availability = await _guard.GetAvailabilityAsync(workspaceId, ct);
        if (availability.IsUsable(plugin))
            return Result.Failure<WorkspacePluginRequestDto>(
                $"{plugin.Label} is already available in this workspace.",
                WorkspacePluginConstants.ErrorCodes.PluginAlreadyAvailable);

        // plugin_requests_one_pending enforces this too; asking first turns a constraint violation
        // into a 409 the page can explain.
        var duplicate = await _unitOfWork.PluginRequestRepository.AnyAsync(
            r => r.WorkspaceId == workspaceId
                && r.PluginId == plugin.Id
                && r.RequestedBy == callerId
                && r.Status == WorkspacePluginConstants.RequestStatus.Pending,
            ct);
        if (duplicate)
            return Result.Failure<WorkspacePluginRequestDto>(
                $"You have already asked for {plugin.Label}. Your workspace owner has not decided yet.",
                WorkspacePluginConstants.ErrorCodes.RequestAlreadyPending);

        var entity = new PluginRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PluginId = plugin.Id,
            RequestedBy = callerId,
            Reason = reason,
            Status = WorkspacePluginConstants.RequestStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        await _unitOfWork.PluginRequestRepository.AddAsync(entity, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // After the commit, and best-effort: a lost notification leaves the request on the Owner's
        // Plugins page and on the sidebar's count, which is where it would be found anyway.
        var workspace = await _directoryClient.GetProfileAsync(workspaceId, ct);
        if (workspace?.OwnerUserId is { } ownerUserId && ownerUserId != callerId)
        {
            await SendAsync(
                PluginRequestNotifications.Requested(workspace, ownerUserId, entity, plugin, requesterEmail),
                ct);
        }
        else if (workspace is null)
        {
            _logger.LogWarning(
                "Plugin request {RequestId} saved, but workspace {WorkspaceId} could not be resolved to notify its owner.",
                entity.Id,
                workspaceId);
        }

        return Result.Success(ToRequestDto(entity, plugin));
    }

    public Task<Result<WorkspacePluginRequestDto>> ApproveRequestAsync(
        Guid workspaceId,
        Guid callerId,
        Guid requestId,
        CancellationToken ct = default) =>
        DecideAsync(workspaceId, callerId, requestId, WorkspacePluginConstants.RequestStatus.Approved, ct);

    public Task<Result<WorkspacePluginRequestDto>> DeclineRequestAsync(
        Guid workspaceId,
        Guid callerId,
        Guid requestId,
        CancellationToken ct = default) =>
        DecideAsync(workspaceId, callerId, requestId, WorkspacePluginConstants.RequestStatus.Declined, ct);

    private async Task<Result<WorkspacePluginRequestDto>> DecideAsync(
        Guid workspaceId,
        Guid callerId,
        Guid requestId,
        string decision,
        CancellationToken ct)
    {
        if (!await IsOwnerAsync(workspaceId, callerId, ct))
            return Denied<WorkspacePluginRequestDto>(WorkspacePluginConstants.Messages.OwnerOnly);

        // Scoped by workspace in the lookup: a request id from another workspace is unknown here,
        // not forbidden, so an Owner cannot decide - or learn about - someone else's.
        var request = await _unitOfWork.PluginRequestRepository.FirstOrDefaultAsync(
            r => r.Id == requestId && r.WorkspaceId == workspaceId,
            ct: ct);
        if (request is null)
            return Result.Failure<WorkspacePluginRequestDto>(
                "Unknown plugin request.",
                WorkspacePluginConstants.ErrorCodes.UnknownRequest);

        if (request.Status != WorkspacePluginConstants.RequestStatus.Pending)
            return Result.Failure<WorkspacePluginRequestDto>(
                "This request has already been decided.",
                WorkspacePluginConstants.ErrorCodes.RequestNotPending);

        var plugin = await _unitOfWork.PluginRepository.GetByIdAsync(request.PluginId, ct);
        if (plugin is null) return UnknownPlugin<WorkspacePluginRequestDto>();

        IReadOnlyList<PluginRequest> settled;
        if (decision == WorkspacePluginConstants.RequestStatus.Approved)
        {
            var added = await AddToListAsync(workspaceId, callerId, plugin, ct);
            if (!added.IsSuccess) return Result.Failure<WorkspacePluginRequestDto>(added.Error!, added.ErrorCode);

            // Every pending request for the plugin is answered by adding it, not only this one.
            settled = await SettlePendingRequestsAsync(workspaceId, plugin.Id, callerId, decision, ct);
        }
        else
        {
            Settle(request, callerId, decision);
            settled = [request];
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await NotifyDecidedAsync(workspaceId, settled, plugin, ct);

        return Result.Success(ToRequestDto(request, plugin));
    }

    // ---- the list, and the transition onto it ----------------------------------------------------

    /// <summary>
    /// Puts a marketplace plugin on the workspace's list, curating the workspace first if it was
    /// still on the pre-marketplace default. Staged, not saved.
    /// </summary>
    private async Task<Result<WorkspacePlugin>> AddToListAsync(
        Guid workspaceId,
        Guid callerId,
        Plugin plugin,
        CancellationToken ct)
    {
        if (plugin.OwnerWorkspaceId is not null)
            return Result.Failure<WorkspacePlugin>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // A retired row cannot be newly added. Rows already on a list stay there untouched - the
        // plugin is inactive, so nothing can use it, but reactivating it restores them as they were.
        if (!plugin.IsActive)
            return Result.Failure<WorkspacePlugin>(
                $"{plugin.Label} has been retired from the marketplace and can no longer be added.",
                WorkspacePluginConstants.ErrorCodes.PluginRetired);

        var curated = await EnsureCuratedAsync(workspaceId, callerId, ct);
        if (!curated.IsSuccess) return Result.Failure<WorkspacePlugin>(curated.Error!, curated.ErrorCode);

        var existing = curated.Value!.FirstOrDefault(row => row.PluginId == plugin.Id)
            ?? await _unitOfWork.WorkspacePluginRepository.FirstOrDefaultAsync(
                row => row.WorkspaceId == workspaceId && row.PluginId == plugin.Id,
                ct: ct);
        if (existing is not null)
        {
            // A seeded row was not a person's decision; this one is.
            existing.AddedBy ??= callerId;
            return Result.Success(existing);
        }

        var row = new WorkspacePlugin
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PluginId = plugin.Id,
            AddedBy = callerId,
            AddedAt = DateTime.UtcNow,
        };
        await _unitOfWork.WorkspacePluginRepository.AddAsync(row, ct);
        return Result.Success(row);
    }

    /// <summary>
    /// The transition. A workspace with no curation row is still judged by AllowAnyPlugins; the
    /// first write to its list records the curation and, if the switch was on, seeds the list with
    /// every active marketplace plugin - so the change the Owner is making is the ONLY change.
    /// </summary>
    /// <remarks>
    /// Refuses, rather than guesses, when the workspace service cannot say what the switch was.
    /// This is the one read of AllowAnyPlugins that is written down: once the curation row exists
    /// the switch is never consulted again, so an outage read as "off" here would not be a
    /// momentary refusal but a permanent, empty list - every member losing every plugin because
    /// the Owner happened to click during a blip. Nothing is staged before the answer is known.
    /// </remarks>
    /// <returns>
    /// The rows seeded by this call (staged, unsaved), empty if nothing was seeded; or
    /// <see cref="WorkspacePluginConstants.ErrorCodes.PolicyUnavailable"/>.
    /// </returns>
    private async Task<Result<IReadOnlyList<WorkspacePlugin>>> EnsureCuratedAsync(
        Guid workspaceId,
        Guid callerId,
        CancellationToken ct)
    {
        if (await _unitOfWork.WorkspacePluginCurationRepository.GetByIdAsync(workspaceId, ct) is not null)
            return Result.Success<IReadOnlyList<WorkspacePlugin>>([]);

        var answer = await _policyClient.ReadAllowAnyPluginsAsync(workspaceId, ct);
        if (answer == WorkspacePluginPolicyAnswer.Unknown)
        {
            _logger.LogWarning(
                "Refused the first edit of workspace {WorkspaceId}'s plugin list: AllowAnyPlugins could not be read, and seeding from a guess would be permanent.",
                workspaceId);
            return Result.Failure<IReadOnlyList<WorkspacePlugin>>(
                WorkspacePluginConstants.Messages.PolicyUnavailable,
                WorkspacePluginConstants.ErrorCodes.PolicyUnavailable);
        }

        var allowedEverything = answer == WorkspacePluginPolicyAnswer.Allowed;
        var now = DateTime.UtcNow;

        await _unitOfWork.WorkspacePluginCurationRepository.AddAsync(new WorkspacePluginCuration
        {
            WorkspaceId = workspaceId,
            CuratedAt = now,
            CuratedBy = callerId,
            SeededFromAllowAnyPlugins = allowedEverything,
        }, ct);

        if (!allowedEverything) return Result.Success<IReadOnlyList<WorkspacePlugin>>([]);

        var marketplace = await _unitOfWork.PluginRepository.FindAsync(
            p => p.IsActive && p.OwnerWorkspaceId == null,
            ct: ct);
        var seeded = new List<WorkspacePlugin>(marketplace.Count);
        foreach (var plugin in marketplace)
        {
            var row = new WorkspacePlugin
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                PluginId = plugin.Id,
                // Null: nobody chose this row. The page can tell a seeded plugin from an added one.
                AddedBy = null,
                AddedAt = now,
            };
            await _unitOfWork.WorkspacePluginRepository.AddAsync(row, ct);
            seeded.Add(row);
        }

        _logger.LogInformation(
            "Workspace {WorkspaceId} curated its plugin list for the first time; seeded {Count} marketplace plugin(s) from AllowAnyPlugins=true.",
            workspaceId,
            marketplace.Count);

        return Result.Success<IReadOnlyList<WorkspacePlugin>>(seeded);
    }

    private async Task<IReadOnlyList<PluginRequest>> SettlePendingRequestsAsync(
        Guid workspaceId,
        Guid pluginId,
        Guid callerId,
        string decision,
        CancellationToken ct)
    {
        var pending = await _unitOfWork.PluginRequestRepository.FindAsync(
            r => r.WorkspaceId == workspaceId
                && r.PluginId == pluginId
                && r.Status == WorkspacePluginConstants.RequestStatus.Pending,
            ct: ct);
        foreach (var request in pending) Settle(request, callerId, decision);
        return pending;
    }

    private void Settle(PluginRequest request, Guid callerId, string decision)
    {
        request.Status = decision;
        request.DecidedBy = callerId;
        request.DecidedAt = DateTime.UtcNow;
        _unitOfWork.PluginRequestRepository.Update(request);
    }

    private async Task NotifyDecidedAsync(
        Guid workspaceId,
        IReadOnlyList<PluginRequest> settled,
        Plugin plugin,
        CancellationToken ct)
    {
        if (settled.Count == 0) return;

        var workspace = await _directoryClient.GetProfileAsync(workspaceId, ct);
        if (workspace is null)
        {
            _logger.LogWarning(
                "Decided {Count} plugin request(s) in workspace {WorkspaceId}, but could not resolve the workspace to notify the requesters.",
                settled.Count,
                workspaceId);
            return;
        }

        foreach (var request in settled)
            await SendAsync(PluginRequestNotifications.Decided(workspace, request, plugin), ct);
    }

    private async Task SendAsync(UserNotification notification, CancellationToken ct)
    {
        if (!await _notificationClient.SendAsync(notification, ct))
        {
            _logger.LogWarning(
                "Notification {Type} to user {UserId} was not accepted by the notification service.",
                notification.Type,
                notification.UserId);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<IReadOnlyList<WorkspacePluginRequestDto>> ListRequestsAsync(Guid workspaceId, CancellationToken ct)
    {
        var requests = await _unitOfWork.PluginRequestRepository.ListForWorkspaceAsync(
            workspaceId,
            WorkspacePluginConstants.RequestStatus.Pending,
            ct);
        return await ToRequestDtosAsync(requests, ct);
    }

    private async Task<IReadOnlyList<WorkspacePluginRequestDto>> ToRequestDtosAsync(
        IReadOnlyList<PluginRequest> requests,
        CancellationToken ct)
    {
        if (requests.Count == 0) return [];

        var pluginIds = requests.Select(r => r.PluginId).Distinct().ToList();
        var plugins = (await _unitOfWork.PluginRepository.FindAsync(p => pluginIds.Contains(p.Id), ct: ct))
            .ToDictionary(p => p.Id);

        return requests
            .Where(r => plugins.ContainsKey(r.PluginId))
            .Select(r => ToRequestDto(r, plugins[r.PluginId]))
            .ToList();
    }

    private static WorkspacePluginRequestDto ToRequestDto(PluginRequest request, Plugin plugin) =>
        new(
            request.Id,
            request.WorkspaceId,
            plugin.PluginKey,
            plugin.Label,
            plugin.AvatarUrl,
            request.RequestedBy,
            request.Reason,
            request.Status,
            request.CreatedAt,
            request.DecidedBy,
            request.DecidedAt);

    private static WorkspacePluginItemDto ToItemDto(
        Plugin plugin,
        string availability,
        WorkspacePlugin? row,
        int membersUsed) =>
        new(
            plugin.PluginKey,
            plugin.Provider,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            plugin.Kind,
            availability,
            plugin.OwnerWorkspaceId is null ? null : plugin.McpServerUrl,
            plugin.OwnerWorkspaceId is null ? row?.AddedBy : plugin.CreatedBy,
            plugin.OwnerWorkspaceId is null ? row?.AddedAt : plugin.CreatedAt,
            membersUsed);

    private static Result ValidatePrivateFields(string label, string description)
    {
        if (label.Length == 0)
            return Result.Failure("Give the plugin a name.", WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);
        if (label.Length > McpPluginRows.MaxLabelLength)
            return Result.Failure(
                $"Keep the name under {McpPluginRows.MaxLabelLength} characters.",
                WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);
        if (description.Length > McpPluginRows.MaxDescriptionLength)
            return Result.Failure(
                $"Keep the description under {McpPluginRows.MaxDescriptionLength} characters.",
                WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin);
        return Result.Success();
    }

    private async Task<bool> IsActiveMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, userId, ct);
        return membership.IsMember && membership.IsActive;
    }

    private async Task<bool> IsOwnerAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        IsOwner(await _membershipClient.GetMembershipAsync(workspaceId, userId, ct));

    private static bool IsOwner(WorkspaceMembership membership) =>
        membership.IsMember
        && membership.IsActive
        && string.Equals(membership.RoleName, WorkspaceRoleConstants.Owner, StringComparison.OrdinalIgnoreCase);

    private static Result<T> Denied<T>(string message) =>
        Result.Failure<T>(message, PluginConstants.ErrorCodes.PermissionDenied);

    private static Result<T> UnknownPlugin<T>() =>
        Result.Failure<T>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);
}

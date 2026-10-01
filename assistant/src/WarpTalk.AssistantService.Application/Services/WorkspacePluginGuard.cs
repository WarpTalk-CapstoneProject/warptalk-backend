using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IWorkspacePluginGuard"/>
public class WorkspacePluginGuard : IWorkspacePluginGuard
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspacePluginPolicyClient _policyClient;
    private readonly IWorkspaceMembershipClient _membershipClient;

    public WorkspacePluginGuard(
        IUnitOfWork unitOfWork,
        IWorkspacePluginPolicyClient policyClient,
        IWorkspaceMembershipClient membershipClient)
    {
        _unitOfWork = unitOfWork;
        _policyClient = policyClient;
        _membershipClient = membershipClient;
    }

    public Task<WorkspacePluginAvailability> GetAvailabilityAsync(Guid workspaceId, CancellationToken ct = default) =>
        ReadAvailabilityAsync(workspaceId, callerIsOwner: false, ct);

    private async Task<WorkspacePluginAvailability> ReadAvailabilityAsync(
        Guid workspaceId,
        bool callerIsOwner,
        CancellationToken ct)
    {
        // The platform layer first: what a platform admin decided for this workspace, and its plan
        // when a plan rule needs one. See PluginWorkspaceAccess for the order the layers apply in.
        var (overrides, planSlug) = await ReadPlatformLayerAsync(workspaceId, ct);

        var curation = await _unitOfWork.WorkspacePluginCurationRepository.GetByIdAsync(workspaceId, ct);
        if (curation is not null)
        {
            var rows = await _unitOfWork.WorkspacePluginRepository.FindAsync(row => row.WorkspaceId == workspaceId, ct: ct);
            return new WorkspacePluginAvailability(
                workspaceId,
                isCurated: true,
                rows.Select(row => row.PluginId).ToHashSet(),
                callerIsOwner,
                overrides,
                planSlug);
        }

        // Not curated yet: what the workspace carries over - the plugins its members already use
        // here, while the pre-marketplace switch is on. The policy client answers false when the
        // workspace service cannot be reached, so an outage reads as "no plugins", never as "some".
        var allowsPlugins = await _policyClient.AllowsPluginUsageAsync(workspaceId, ct);
        var used = allowsPlugins
            ? await _unitOfWork.PluginToolAuditRepository.GetPluginIdsUsedInWorkspaceAsync(workspaceId, ct)
            : new HashSet<Guid>();
        return new WorkspacePluginAvailability(
            workspaceId,
            isCurated: false,
            WorkspacePluginAvailability.CarriedOver(allowsPlugins, used),
            callerIsOwner,
            overrides,
            planSlug);
    }

    /// <summary>
    /// The workspace's overrides, and its plan - read only when some marketplace plugin has a plan
    /// rule, so the common case (no plan rules anywhere) costs no workspace-service call.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, WorkspacePluginOverride> Overrides, string? PlanSlug)> ReadPlatformLayerAsync(
        Guid workspaceId,
        CancellationToken ct)
    {
        var overrides = (await _unitOfWork.WorkspacePluginOverrideRepository.FindAsync(
                row => row.WorkspaceId == workspaceId, ct: ct))
            .GroupBy(row => row.PluginId)
            .ToDictionary(group => group.Key, group => group.First());

        var anyPlanRule = await _unitOfWork.PluginRepository.AnyAsync(
            plugin => plugin.OwnerWorkspaceId == null && plugin.IsActive && plugin.AllowedPlanSlugsJson != null,
            ct);
        var planSlug = anyPlanRule ? await _policyClient.ReadPlanSlugAsync(workspaceId, ct) : null;

        return (overrides, planSlug);
    }

    public async Task<Result<WorkspacePluginAvailability>> GetAvailabilityForMemberAsync(
        Guid? workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        if (!workspaceId.HasValue)
            return Result.Failure<WorkspacePluginAvailability>(
                PluginConstants.WorkspacePolicyMessages.WorkspaceRequired,
                PluginConstants.ErrorCodes.PermissionDenied);

        // Membership before the list, so a caller cannot probe which plugins an arbitrary workspace
        // has by watching which refusal comes back.
        var membership = await _membershipClient.GetMembershipAsync(workspaceId.Value, userId, ct);
        if (!membership.IsMember || !membership.IsActive)
            return Result.Failure<WorkspacePluginAvailability>(
                PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember,
                PluginConstants.ErrorCodes.PermissionDenied);

        return Result.Success(await ReadAvailabilityAsync(workspaceId.Value, membership.IsOwner, ct));
    }

    public async Task<Result> CanUsePluginAsync(
        Guid? workspaceId,
        Guid userId,
        Plugin plugin,
        CancellationToken ct = default)
    {
        if (plugin.OwnerWorkspaceId is { } ownerWorkspaceId)
        {
            if (workspaceId.HasValue && workspaceId.Value != ownerWorkspaceId)
                return Refuse(WorkspacePluginConstants.Messages.PrivatePluginNeedsItsWorkspace);

            return await IsActiveMemberAsync(ownerWorkspaceId, userId, ct)
                ? Result.Success()
                : Refuse(PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember);
        }

        // The legacy personal path, left open on purpose: see IWorkspacePluginGuard.CanUsePluginAsync.
        if (!workspaceId.HasValue) return Result.Success();

        // A named workspace is judged only for someone who belongs to it. Without this a caller
        // could name any workspace that has the plugin and be judged by that one - the borrowed-id
        // hole GetAvailabilityForMemberAsync closes on the tool path. It buys nothing over the
        // no-workspace path today, but it must not become the way around that path once it is
        // closed. Membership first, so the refusal does not reveal which plugins a foreign
        // workspace has.
        if (!await IsActiveMemberAsync(workspaceId.Value, userId, ct))
            return Refuse(PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember);

        var availability = await GetAvailabilityAsync(workspaceId.Value, ct);
        return availability.IsUsable(plugin)
            ? Result.Success()
            : Refuse(RefusalFor(availability, plugin));
    }

    public async Task<Result> CanUsePluginInWorkspaceAsync(
        Guid? workspaceId,
        Guid userId,
        Plugin plugin,
        CancellationToken ct = default)
    {
        var availability = await GetAvailabilityForMemberAsync(workspaceId, userId, ct);
        if (!availability.IsSuccess)
            return Result.Failure(availability.Error!, availability.ErrorCode);

        return availability.Value!.IsUsable(plugin)
            ? Result.Success()
            : Refuse(RefusalFor(availability.Value!, plugin));
    }

    /// <summary>Why a plugin is not usable here, in the words the member sees.</summary>
    private static string RefusalFor(WorkspacePluginAvailability availability, Plugin plugin)
    {
        if (plugin.OwnerWorkspaceId is not null) return WorkspacePluginConstants.Messages.PrivatePluginNeedsItsWorkspace;
        return availability.Of(plugin) == WorkspacePluginConstants.Availability.DisabledByPlatform
            ? PluginWorkspaceAccessConstants.Messages.DisabledByPlatform
            : WorkspacePluginConstants.Messages.NotAdded;
    }

    private async Task<bool> IsActiveMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, userId, ct);
        return membership.IsMember && membership.IsActive;
    }

    private static Result Refuse(string message) =>
        Result.Failure(message, PluginConstants.ErrorCodes.PermissionDenied);
}

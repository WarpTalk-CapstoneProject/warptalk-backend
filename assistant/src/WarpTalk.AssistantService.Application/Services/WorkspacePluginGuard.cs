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

    public async Task<WorkspacePluginAvailability> GetAvailabilityAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var curation = await _unitOfWork.WorkspacePluginCurationRepository.GetByIdAsync(workspaceId, ct);
        if (curation is not null)
        {
            var rows = await _unitOfWork.WorkspacePluginRepository.FindAsync(row => row.WorkspaceId == workspaceId, ct: ct);
            return new WorkspacePluginAvailability(
                workspaceId,
                isCurated: true,
                legacyAllowsEveryPlugin: false,
                rows.Select(row => row.PluginId).ToHashSet());
        }

        // Not curated yet: still the pre-marketplace switch. The policy client answers false when
        // the workspace service cannot be reached, so an outage reads as "no plugins", never as
        // "every plugin".
        var allowsAll = await _policyClient.AllowsPluginUsageAsync(workspaceId, ct);
        return new WorkspacePluginAvailability(
            workspaceId,
            isCurated: false,
            legacyAllowsEveryPlugin: allowsAll,
            new HashSet<Guid>());
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
        if (!await IsActiveMemberAsync(workspaceId.Value, userId, ct))
            return Result.Failure<WorkspacePluginAvailability>(
                PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember,
                PluginConstants.ErrorCodes.PermissionDenied);

        return Result.Success(await GetAvailabilityAsync(workspaceId.Value, ct));
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

        if (!workspaceId.HasValue) return Result.Success();

        var availability = await GetAvailabilityAsync(workspaceId.Value, ct);
        return availability.IsUsable(plugin)
            ? Result.Success()
            : Refuse(WorkspacePluginConstants.Messages.NotAdded);
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
            : Refuse(plugin.OwnerWorkspaceId is null
                ? WorkspacePluginConstants.Messages.NotAdded
                : WorkspacePluginConstants.Messages.PrivatePluginNeedsItsWorkspace);
    }

    private async Task<bool> IsActiveMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, userId, ct);
        return membership.IsMember && membership.IsActive;
    }

    private static Result Refuse(string message) =>
        Result.Failure(message, PluginConstants.ErrorCodes.PermissionDenied);
}

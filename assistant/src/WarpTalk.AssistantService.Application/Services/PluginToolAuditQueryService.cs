using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginToolAuditQueryService : IPluginToolAuditQueryService
{
    /// <summary>Rows returned when the caller asks for no particular page size.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// The most rows one call will return, whatever the caller asks for. An audit table has no
    /// upper bound, so an unclamped page size is a way to ask this service to materialise all of
    /// it.
    /// </summary>
    public const int MaxPageSize = 200;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMembershipClient _membershipClient;

    public PluginToolAuditQueryService(
        IUnitOfWork unitOfWork,
        IWorkspaceMembershipClient membershipClient)
    {
        _unitOfWork = unitOfWork;
        _membershipClient = membershipClient;
    }

    public async Task<Result<IReadOnlyList<PluginToolAuditDto>>> ListWorkspaceAuditsAsync(
        Guid workspaceId,
        Guid callerUserId,
        string? pluginKey,
        Guid? userId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        // Authorised before anything is read. The assistant service holds no workspace roles of
        // its own, so this is the workspace service's answer, and it fails closed when that
        // service cannot be reached.
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerUserId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Result.Failure<IReadOnlyList<PluginToolAuditDto>>(
                "Only a workspace Owner or Admin may read plugin tool usage for a workspace.",
                PluginConstants.ErrorCodes.PermissionDenied);

        var audits = await _unitOfWork.PluginToolAuditRepository.ListForWorkspaceAsync(
            workspaceId,
            pluginKey,
            userId,
            Math.Max(0, skip),
            NormalizePageSize(take),
            ct);

        return Result.Success<IReadOnlyList<PluginToolAuditDto>>(audits.Select(ToDto).ToList());
    }

    private static int NormalizePageSize(int take) =>
        take switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => take,
        };

    private static PluginToolAuditDto ToDto(PluginToolAudit audit) =>
        new(
            audit.Id,
            audit.UserId,
            audit.ConversationId,
            audit.PluginKey,
            audit.ToolName,
            audit.ResultStatus,
            audit.ProviderResourceRef,
            audit.CreatedAt);
}

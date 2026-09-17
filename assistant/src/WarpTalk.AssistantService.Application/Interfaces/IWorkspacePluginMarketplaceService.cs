using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The workspace half of the plugin marketplace: what a workspace has, and members asking for more.
/// </summary>
/// <remarks>
/// AUTHORIZATION, resolved from the workspace service on every call and never from the request:
/// <list type="bullet">
///   <item>Reads of the workspace's list and its pending requests: Owner or Admin.</item>
///   <item>Every write - add, remove, private plugins, approve, decline: Owner only. The owner's
///   decision was "workspace owner có thể add cho workspace của họ".</item>
///   <item>Asking for a plugin and reading one's own requests: any active member.</item>
/// </list>
/// A refusal is <c>PluginConstants.ErrorCodes.PermissionDenied</c> with the same wording whatever the
/// workspace holds, so a non-member learns nothing about it.
/// </remarks>
public interface IWorkspacePluginMarketplaceService
{
    Task<Result<WorkspacePluginsOverviewDto>> GetOverviewAsync(Guid workspaceId, Guid callerId, CancellationToken ct = default);

    Task<Result<WorkspacePluginItemDto>> AddMarketplacePluginAsync(Guid workspaceId, Guid callerId, string pluginKey, CancellationToken ct = default);

    Task<Result> RemovePluginAsync(Guid workspaceId, Guid callerId, string pluginKey, CancellationToken ct = default);

    Task<Result<WorkspacePluginItemDto>> CreatePrivatePluginAsync(Guid workspaceId, Guid callerId, CreatePrivatePluginRequest request, CancellationToken ct = default);

    Task<Result<WorkspacePluginItemDto>> UpdatePrivatePluginAsync(Guid workspaceId, Guid callerId, string pluginKey, UpdatePrivatePluginRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkspacePluginRequestDto>>> ListPendingRequestsAsync(Guid workspaceId, Guid callerId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkspacePluginRequestDto>>> ListMyRequestsAsync(Guid workspaceId, Guid callerId, CancellationToken ct = default);

    /// <param name="requesterEmail">For the Owner's notification; the service holds no user directory.</param>
    Task<Result<WorkspacePluginRequestDto>> CreateRequestAsync(Guid workspaceId, Guid callerId, string? requesterEmail, CreatePluginRequestRequest request, CancellationToken ct = default);

    Task<Result<WorkspacePluginRequestDto>> ApproveRequestAsync(Guid workspaceId, Guid callerId, Guid requestId, CancellationToken ct = default);

    Task<Result<WorkspacePluginRequestDto>> DeclineRequestAsync(Guid workspaceId, Guid callerId, Guid requestId, CancellationToken ct = default);
}

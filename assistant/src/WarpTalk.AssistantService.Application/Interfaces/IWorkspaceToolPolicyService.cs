using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The workspace Owner's per-tool rules for WarpBot: ask every time, or never.
/// </summary>
public interface IWorkspaceToolPolicyService
{
    /// <summary>The plugin's tools with this workspace's rules. Owner or Admin.</summary>
    Task<Result<WorkspaceToolPoliciesDto>> GetAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default);

    /// <summary>Sets (<c>approval</c> / <c>blocked</c>) or clears (null) one tool's rule. Owner.</summary>
    Task<Result<WorkspaceToolPoliciesDto>> SetAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        UpdateWorkspaceToolPolicyRequest request,
        CancellationToken ct = default);
}

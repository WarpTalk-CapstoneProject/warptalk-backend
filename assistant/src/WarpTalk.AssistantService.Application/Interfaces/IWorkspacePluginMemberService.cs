using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Which of a workspace's members have connected one of its plugins - the Owner's and Admin's
/// "who uses this" view on the workspace Plugins page.
/// </summary>
public interface IWorkspacePluginMemberService
{
    /// <summary>
    /// Members of <paramref name="workspaceId"/> who have <paramref name="pluginKey"/> connected, most
    /// recently used first. Owner or Admin of that workspace; connection metadata only, never tokens.
    /// </summary>
    Task<Result<IReadOnlyList<WorkspacePluginMemberDto>>> ListConnectedMembersAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default);
}

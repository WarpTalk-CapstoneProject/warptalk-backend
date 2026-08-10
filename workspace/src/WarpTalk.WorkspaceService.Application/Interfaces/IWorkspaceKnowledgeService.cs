using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Lets a workspace Owner or Admin see what the system has indexed about their workspace.
///
/// Every method takes the workspace and the caller explicitly. There is no ambient tenant:
/// the workspace comes from the route and the caller from the token, and the two are checked
/// against workspace_members on every call. Getting that wrong here means showing one
/// workspace's document contents to another.
/// </summary>
public interface IWorkspaceKnowledgeService
{
    Task<Result<WorkspaceKnowledgePageDto>> GetKnowledgeAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// The same listing for the System Admin portal, which is global by construction and
    /// consults no membership — the caller is gated by the system-admin policy at the
    /// controller, exactly as <see cref="IAdminWorkspaceService"/> documents.
    /// </summary>
    Task<Result<WorkspaceKnowledgePageDto>> GetKnowledgeForAdminAsync(
        Guid workspaceId,
        GetWorkspaceKnowledgeQuery query,
        CancellationToken ct = default);
}

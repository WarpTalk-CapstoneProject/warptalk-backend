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

    /// <summary>
    /// Corrects one chunk's fact, its category, and whether WarpBot may retrieve it.
    ///
    /// Owner only, where the listing is Owner OR Admin. Reading what the workspace knows and
    /// deciding what it is allowed to know are different acts: an Admin runs the workspace
    /// day to day, and editing what the assistant will tell everyone — or removing the
    /// evidence of what it was told — belongs to the person who answers for the workspace.
    /// </summary>
    Task<Result<WorkspaceKnowledgeChunkDto>> UpdateKnowledgeChunkAsync(
        Guid workspaceId,
        string chunkId,
        UpdateWorkspaceKnowledgeChunkRequest request,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Removes one chunk from the index. Owner only, for the same reason.
    ///
    /// The source is untouched: deleting a chunk of a document does not delete the document,
    /// and re-uploading it will index it again. This is a statement about what the assistant
    /// may draw on, not a retention operation.
    /// </summary>
    Task<Result<bool>> DeleteKnowledgeChunkAsync(
        Guid workspaceId,
        string chunkId,
        Guid userId,
        CancellationToken ct = default);
}

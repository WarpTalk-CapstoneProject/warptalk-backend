using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceDocumentService
{
    Task<Result<WorkspaceDocumentDto>> UploadDocumentAsync(Guid workspaceId, UploadDocumentApiRequest request, Guid userId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceDocumentDto>>> ListDocumentsAsync(Guid workspaceId, GetDocumentsQuery query, Guid userId, CancellationToken ct = default);
    Task<Result<WorkspaceDocumentDto>> GetDocumentByIdAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
    Task<Result<WorkspaceDocumentDto>> PatchDocumentMetadataAsync(Guid workspaceId, Guid documentId, PatchDocumentRequest request, Guid userId, CancellationToken ct = default);
    Task<Result> AddAccessPolicyAsync(Guid workspaceId, Guid documentId, AddAccessPolicyRequest request, Guid userId, CancellationToken ct = default);
    Task<Result> RemoveAccessPolicyAsync(Guid workspaceId, Guid documentId, Guid policyId, Guid userId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceDocumentAccessPolicyDto>>> GetAccessPoliciesAsync(Guid workspaceId, Guid documentId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> ApproveDocumentAsync(Guid workspaceId, Guid documentId, ApproveDocumentRequest request, Guid userId, CancellationToken ct = default);
    Task<Result<WorkspaceDocumentDto>> DownloadDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
    Task<Result<ExtractedTextDto>> GetExtractedTextAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
    Task<Result> DeleteDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
    Task<Result> ArchiveDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
    Task<Result> RestoreDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default);
}

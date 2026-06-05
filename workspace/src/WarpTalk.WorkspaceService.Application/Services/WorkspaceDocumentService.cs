using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceDocumentService : IWorkspaceDocumentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentAccessEvaluator _accessEvaluator;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceUrlProvider _urlProvider;
    private readonly ILogger<WorkspaceDocumentService> _logger;

    public WorkspaceDocumentService(
        IUnitOfWork unitOfWork,
        IDocumentAccessEvaluator accessEvaluator,
        IWorkspaceDocumentEventPublisher eventPublisher,
        IAuthIdentityClient authIdentity,
        IWorkspaceUrlProvider urlProvider,
        ILogger<WorkspaceDocumentService> logger)
    {
        _unitOfWork = unitOfWork;
        _accessEvaluator = accessEvaluator;
        _eventPublisher = eventPublisher;
        _authIdentity = authIdentity;
        _urlProvider = urlProvider;
        _logger = logger;
    }

    public async Task<Result<WorkspaceDocumentDto>> UploadDocumentAsync(Guid workspaceId, UploadDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null || !workspace.IsActive || workspace.DeletedAt != null)
        {
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
        }

        var isOwnerOrAdmin = false;
        var role = await _authIdentity.GetRoleByIdAsync(member.RoleId, ct);
        if (role != null)
        {
            isOwnerOrAdmin = role.Name.IsOwnerOrAdmin();
        }

        var docId = Guid.NewGuid();
        var storageKey = WorkspaceDocumentHelper.GenerateStorageKey(workspaceId, docId, request.FileExtension);

        var document = request.ToEntity(docId, workspaceId, userId, storageKey, isOwnerOrAdmin);

        await _unitOfWork.WorkspaceDocumentRepository.AddAsync(document, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        if (isOwnerOrAdmin)
        {
            // Publish event to Redis Stream for AI Ingestion
            await _eventPublisher.PublishDocumentUploadedAsync(
                document.Id,
                workspaceId,
                document.StorageKey,
                document.FileName,
                document.FileExtension,
                userId,
                document.IsSensitive,
                ct);
        }

        await AuditAsync(document.Id, workspaceId, userId, "UploadDocument", new { document.Name, document.IsSensitive });

        return Result.Success(document.ToDto(_urlProvider));
    }

    public async Task<Result<PagedResult<WorkspaceDocumentDto>>> ListDocumentsAsync(Guid workspaceId, GetDocumentsQuery query, Guid userId, CancellationToken ct = default)
    {
        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null || !workspace.IsActive || workspace.DeletedAt != null)
        {
            return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
        }

        var documents = await _unitOfWork.WorkspaceDocumentRepository.FindAsync(
            d => d.WorkspaceId == workspaceId && d.DeletedAt == null, "", ct);

        var filteredDocs = documents.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            filteredDocs = filteredDocs.Where(d =>
                d.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                d.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var allowedDtos = new List<WorkspaceDocumentDto>();
        foreach (var doc in filteredDocs)
        {
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, doc.Id, "view", ct);
            if (accessResult.IsSuccess)
            {
                allowedDtos.Add(doc.ToDto(_urlProvider));
            }
        }

        var totalCount = allowedDtos.Count;
        var paginatedItems = allowedDtos
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        var pagedResult = new PagedResult<WorkspaceDocumentDto>(paginatedItems, query.Page, query.PageSize, totalCount);
        return Result.Success(pagedResult);
    }

    public async Task<Result<WorkspaceDocumentDto>> GetDocumentByIdAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "view", ct);
        if (!accessResult.IsSuccess)
        {
            return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
        }

        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null)
        {
            return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
        }

        await AuditAsync(documentId, workspaceId, userId, "GetDocumentDetails");

        return Result.Success(document.ToDto(_urlProvider));
    }

    public async Task<Result<WorkspaceDocumentDto>> PatchDocumentMetadataAsync(Guid workspaceId, Guid documentId, PatchDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
        if (!canManage)
        {
            return Result.Failure<WorkspaceDocumentDto>("Forbidden. Only owner or admin can edit document metadata.", ErrorCodes.Forbidden);
        }

        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null || document.DeletedAt != null)
        {
            return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
        }

        if (request.Name != null)
        {
            document.Name = request.Name;
        }

        if (request.IsSensitive.HasValue)
        {
            document.IsSensitive = request.IsSensitive.Value;
            document.ConfidentialityLevel = WorkspaceDocumentHelper.GetConfidentialityLevel(request.IsSensitive.Value);
        }

        document.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.WorkspaceDocumentRepository.Update(document);
        await _unitOfWork.SaveChangesAsync(ct);

        await AuditAsync(documentId, workspaceId, userId, "PatchDocumentMetadata", request);

        return Result.Success(document.ToDto(_urlProvider));
    }

    public async Task<Result> ManageAccessPolicyAsync(Guid workspaceId, Guid documentId, ManageAccessPolicyRequest request, Guid userId, CancellationToken ct = default)
    {
        var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
        if (!canManage)
        {
            return Result.Failure("Forbidden. Only owner or admin can manage document access policies.", ErrorCodes.Forbidden);
        }

        if (string.Equals(request.Action, "Add", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.SubjectType))
            {
                return Result.Failure("SubjectType is required for Add action.");
            }
            if (string.IsNullOrWhiteSpace(request.Permission))
            {
                return Result.Failure("Permission is required for Add action.");
            }
            if (string.IsNullOrWhiteSpace(request.Effect))
            {
                return Result.Failure("Effect is required for Add action.");
            }

            var policy = request.ToEntity(documentId, workspaceId, userId);

            await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.AddAsync(policy, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await AuditAsync(documentId, workspaceId, userId, "AddAccessPolicy", new { policy.Id, policy.SubjectType, policy.SubjectKey, policy.Permission, policy.Effect });

            return Result.Success();
        }
        else if (string.Equals(request.Action, "Remove", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.PolicyId.HasValue)
            {
                return Result.Failure("PolicyId is required for Remove action.");
            }

            var policy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.GetByIdAsync(request.PolicyId.Value, ct);
            if (policy == null || policy.DocumentId != documentId)
            {
                return Result.Failure("Policy not found or does not belong to this document.", ErrorCodes.NotFound);
            }

            _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Remove(policy);
            await _unitOfWork.SaveChangesAsync(ct);

            await AuditAsync(documentId, workspaceId, userId, "RemoveAccessPolicy", new { policy.Id });

            return Result.Success();
        }

        return Result.Failure("Invalid action. Supported actions are 'Add' and 'Remove'.");
    }

    public async Task<Result<List<WorkspaceDocumentAccessPolicyDto>>> GetAccessPoliciesAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
        if (!canManage)
        {
            return Result.Failure<List<WorkspaceDocumentAccessPolicyDto>>("Forbidden. Only owner or admin can view access policies.", ErrorCodes.Forbidden);
        }

        var policies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository
            .FindAsync(p => p.DocumentId == documentId, "", ct);

        var dtos = policies.Select(p => p.ToDto()).ToList();

        return Result.Success(dtos);
    }

    public async Task<Result> ApproveDocumentAsync(Guid workspaceId, Guid documentId, ApproveDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var role = await _authIdentity.GetRoleByIdAsync(member.RoleId, ct);
            if (role == null || !role.Name.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            if (!string.Equals(document.Status, WorkspaceDocumentStatus.pending_approval.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("Document is not pending approval.", ErrorCodes.ValidationError);
            }

            if (request.Approve)
            {
                document.Status = WorkspaceDocumentStatus.active.ToString();
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
                document.AiEligible = true;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                await _unitOfWork.SaveChangesAsync(ct);

                // Publish event to Redis Stream for AI Ingestion
                await _eventPublisher.PublishDocumentUploadedAsync(
                    document.Id,
                    workspaceId,
                    document.StorageKey,
                    document.FileName,
                    document.FileExtension,
                    document.UploadedBy ?? userId,
                    document.IsSensitive,
                    ct);

                await AuditAsync(document.Id, workspaceId, userId, "ApproveDocument");
            }
            else
            {
                document.Status = WorkspaceDocumentStatus.rejected.ToString();
                document.AiEligible = false;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                await _unitOfWork.SaveChangesAsync(ct);

                await AuditAsync(document.Id, workspaceId, userId, "RejectDocument");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while approving/rejecting document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDocumentDto>> DownloadDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "download", ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
            }

            await AuditAsync(documentId, workspaceId, userId, "DownloadDocument");

            return Result.Success(document.ToDto(_urlProvider));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while downloading document. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> DeleteDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            // Verify user is Owner/Admin or Document Owner
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var role = await _authIdentity.GetRoleByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = role != null && role.Name.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure("Forbidden. Only owner, admin, or document owner can delete.", ErrorCodes.Forbidden);
            }

            document.DeletedAt = DateTime.UtcNow;
            document.DeletedBy = userId;
            document.AiEligible = false;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);

            // Publish deletion event to Redis Stream to invalidate embeddings
            await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);

            await AuditAsync(documentId, workspaceId, userId, "DeleteDocument");

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    private async Task AuditAsync(Guid documentId, Guid workspaceId, Guid? actorId, string action, object? metadata = null)
    {
        try
        {
            var audit = new WorkspaceDocumentAudit
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                WorkspaceId = workspaceId,
                ActorId = actorId,
                Action = action,
                ActionAt = DateTime.UtcNow,
                Metadata = metadata != null ? JsonSerializer.Serialize(metadata) : null
            };
            await _unitOfWork.WorkspaceDocumentAuditRepository.AddAsync(audit);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write document audit log. DocumentId: {DocumentId}, Action: {Action}", documentId, action);
        }
    }
}

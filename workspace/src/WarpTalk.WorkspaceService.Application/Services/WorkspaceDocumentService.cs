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
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceDocumentService : IWorkspaceDocumentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentAccessEvaluator _accessEvaluator;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceUrlProvider _urlProvider;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceDocumentStorage _storage;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly ILogger<WorkspaceDocumentService> _logger;

    public WorkspaceDocumentService(
        IUnitOfWork unitOfWork,
        IDocumentAccessEvaluator accessEvaluator,
        IWorkspaceDocumentEventPublisher eventPublisher,
        IAuthIdentityClient authIdentity,
        IWorkspaceUrlProvider urlProvider,
        ITranslationRoomClient translationRoomClient,
        IWorkspaceDocumentStorage storage,
        IDocumentTextExtractor textExtractor,
        ILogger<WorkspaceDocumentService> logger)
    {
        _unitOfWork = unitOfWork;
        _accessEvaluator = accessEvaluator;
        _eventPublisher = eventPublisher;
        _authIdentity = authIdentity;
        _urlProvider = urlProvider;
        _translationRoomClient = translationRoomClient;
        _storage = storage;
        _textExtractor = textExtractor;
        _logger = logger;
    }

    public async Task<Result<WorkspaceDocumentDto>> UploadDocumentAsync(Guid workspaceId, UploadDocumentApiRequest request, Guid userId, CancellationToken ct = default)
    {
        try
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

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();

            var docId = Guid.NewGuid();
            var extension = System.IO.Path.GetExtension(request.File.FileName);
            var storageKey = WorkspaceDocumentHelper.GenerateStorageKey(workspaceId, docId, extension);

            var status = isOwnerOrAdmin
                ? WorkspaceDocumentStatus.active
                : WorkspaceDocumentStatus.pending_approval;

            var ingestionStatus = isOwnerOrAdmin
                ? WorkspaceDocumentIngestionStatus.pending
                : WorkspaceDocumentIngestionStatus.awaiting_approval;

            var aiEligible = isOwnerOrAdmin;

            var document = request.ToEntity(docId, workspaceId, userId, storageKey, status, ingestionStatus, aiEligible);

            // Save the document content securely to physical storage (AES-256 + HMAC-SHA512) before DB transaction
            using (var stream = request.File.OpenReadStream())
            {
                await _storage.SaveDocumentContentAsync(document, stream, ct);
            }

            await _unitOfWork.WorkspaceDocumentRepository.AddAsync(document, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            if (isOwnerOrAdmin)
            {
                // Publish upload event to trigger Pre-Ingestion security scan and eventual Qdrant sync
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

            await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.UploadDocument, new { document.Name, document.IsSensitive }, _logger, ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while uploading document. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceDocumentDto>>> ListDocumentsAsync(Guid workspaceId, GetDocumentsQuery query, Guid userId, CancellationToken ct = default)
    {
        try
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

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);

            var allPolicies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FindAsync(
                p => p.WorkspaceId == workspaceId, "", ct);
            var policiesByDoc = allPolicies.ToLookup(p => p.DocumentId);

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

            Dictionary<Guid, TranslationRoomDto?>? roomCache = null;
            Dictionary<Guid, List<TranslationRoomParticipantDto>>? participantsCache = null;

            if (string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                var meetingIds = filteredDocs
                    .Where(d => string.Equals(d.SourceType, WorkspaceDocumentConstants.SourceTypeMeeting, StringComparison.OrdinalIgnoreCase) && d.SourceId.HasValue)
                    .Select(d => d.SourceId!.Value)
                    .Distinct()
                    .ToList();

                if (meetingIds.Any())
                {
                    roomCache = new Dictionary<Guid, TranslationRoomDto?>();
                    participantsCache = new Dictionary<Guid, List<TranslationRoomParticipantDto>>();

                    var roomTasks = meetingIds.Select(async id =>
                    {
                        var room = await _translationRoomClient.GetTranslationRoomAsync(id, ct);
                        return (id, room);
                    }).ToList();

                    var participantTasks = meetingIds.Select(async id =>
                    {
                        var participants = await _translationRoomClient.GetParticipantsAsync(id, ct);
                        return (id, participants);
                    }).ToList();

                    await Task.WhenAll(roomTasks.Cast<Task>().Concat(participantTasks.Cast<Task>()));

                    foreach (var task in roomTasks)
                    {
                        var res = await task;
                        roomCache[res.id] = res.room;
                    }

                    foreach (var task in participantTasks)
                    {
                        var res = await task;
                        participantsCache[res.id] = res.participants;
                    }
                }
            }

            var allowedDtos = new List<WorkspaceDocumentDto>();
            foreach (var doc in filteredDocs)
            {
                var docPolicies = policiesByDoc[doc.Id];
                var accessResult = await _accessEvaluator.EvaluateAccessAsync(
                    userId,
                    workspaceId,
                    doc,
                    WorkspaceDocumentPermissions.View,
                    member,
                    roleName,
                    docPolicies,
                    roomCache,
                    participantsCache,
                    ct);

                if (accessResult.IsSuccess)
                {
                    var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, doc.Id);
                    allowedDtos.Add(doc.ToDto(downloadUrl));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing documents. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<WorkspaceDocumentDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDocumentDto>> GetDocumentByIdAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
            }

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.GetDocumentDetails, logger: _logger, ct: ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting document details. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDocumentDto>> PatchDocumentMetadataAsync(Guid workspaceId, Guid documentId, PatchDocumentRequest request, Guid userId, CancellationToken ct = default)
    {
        try
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

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.PatchDocumentMetadata, request, _logger, ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while patching document metadata. DocumentId: {DocumentId}", documentId);
            return Result.Failure<WorkspaceDocumentDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AddAccessPolicyAsync(Guid workspaceId, Guid documentId, AddAccessPolicyRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure("Forbidden. Only owner or admin can manage document access policies.", ErrorCodes.Forbidden);
            }

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

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.AddAccessPolicy, new { policy.Id, policy.SubjectType, policy.SubjectKey, policy.Permission, policy.Effect }, _logger, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while adding document access policy. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RemoveAccessPolicyAsync(Guid workspaceId, Guid documentId, Guid policyId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure("Forbidden. Only owner or admin can manage document access policies.", ErrorCodes.Forbidden);
            }

            var policy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.GetByIdAsync(policyId, ct);
            if (policy == null || policy.DocumentId != documentId)
            {
                return Result.Failure("Policy not found or does not belong to this document.", ErrorCodes.NotFound);
            }

            _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Remove(policy);
            await _unitOfWork.SaveChangesAsync(ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.RemoveAccessPolicy, new { policy.Id }, _logger, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while removing document access policy. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceDocumentAccessPolicyDto>>> GetAccessPoliciesAsync(Guid workspaceId, Guid documentId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var canManage = await _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, ct);
            if (!canManage)
            {
                return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>("Forbidden. Only owner or admin can view access policies.", ErrorCodes.Forbidden);
            }

            var policies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository
                .FindAsync(p => p.DocumentId == documentId, "", ct);

            var dtos = policies.Select(p => p.ToDto()).ToList();
            var totalCount = dtos.Count;
            var pagedItems = dtos.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();

            var pagedResult = new PagedResult<WorkspaceDocumentAccessPolicyDto>(pagedItems, query.Page, query.PageSize, totalCount);

            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching document access policies. DocumentId: {DocumentId}", documentId);
            return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
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

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
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

                await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.ApproveDocument, logger: _logger, ct: ct);
            }
            else
            {
                document.Status = WorkspaceDocumentStatus.rejected.ToString();
                document.AiEligible = false;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                await _unitOfWork.SaveChangesAsync(ct);

                await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.RejectDocument, logger: _logger, ct: ct);
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
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<WorkspaceDocumentDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null)
            {
                return Result.Failure<WorkspaceDocumentDto>("Document not found.", ErrorCodes.NotFound);
            }

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.DownloadDocument, logger: _logger, ct: ct);

            var downloadUrl = _urlProvider.GetDocumentDownloadUrl(workspaceId, document.Id);
            return Result.Success(document.ToDto(downloadUrl));
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

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
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

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.DeleteDocument, logger: _logger, ct: ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ArchiveDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure("Forbidden. Only owner, admin, or document owner can archive.", ErrorCodes.Forbidden);
            }

            document.Status = WorkspaceDocumentStatus.archived.ToString();
            document.AiEligible = false;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);

            // Publish archived event to Redis Stream to clean up embeddings from Qdrant
            await _eventPublisher.PublishDocumentArchivedAsync(documentId, workspaceId, ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.ArchiveDocument, logger: _logger, ct: ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while archiving document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RestoreDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;

            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure("Forbidden. Only owner, admin, or document owner can restore.", ErrorCodes.Forbidden);
            }

            if (document.Status != WorkspaceDocumentStatus.archived.ToString())
            {
                return Result.Failure("Document is not archived.", ErrorCodes.ValidationError);
            }

            document.Status = WorkspaceDocumentStatus.active.ToString();
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
            document.AiEligible = false; // Scanner will re-evaluate on background security scan

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);

            // Re-publish upload event to trigger security scan and Qdrant index refresh
            await _eventPublisher.PublishDocumentUploadedAsync(
                document.Id,
                workspaceId,
                document.StorageKey,
                document.FileName,
                document.FileExtension,
                document.UploadedBy ?? userId,
                document.IsSensitive,
                ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.RestoreDocument, logger: _logger, ct: ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while restoring document. DocumentId: {DocumentId}", documentId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<ExtractedTextDto>> GetExtractedTextAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<ExtractedTextDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null || document.DeletedAt != null)
            {
                return Result.Failure<ExtractedTextDto>("Document not found.", ErrorCodes.NotFound);
            }

            var extractedText = await _storage.GetExtractedTextAsync(document, ct);
            if (string.IsNullOrEmpty(extractedText))
            {
                ExtractedDocumentContent content;
                using (var decryptedStream = await _storage.GetDecryptedStreamAsync(document, ct))
                {
                    content = await _textExtractor.ExtractTextAsync(decryptedStream, document.FileExtension, ct);
                }
                // Save it so that next time we don't have to extract it on-the-fly (serialized as JSON)
                extractedText = JsonSerializer.Serialize(content);
                await _storage.SaveExtractedTextAsync(document, extractedText, ct);
            }

            ExtractedTextDto textDto;
            if (extractedText.TrimStart().StartsWith("{"))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ExtractedDocumentContent>(extractedText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    textDto = new ExtractedTextDto(
                        parsed?.FullText ?? string.Empty,
                        parsed?.Pages?.Select(p => new ExtractedPageDto(p.PageNumber, p.Text)).ToList() ?? new(),
                        parsed?.Sheets?.Select(s => new ExtractedSheetDto(s.SheetName, s.Rows)).ToList() ?? new()
                    );
                }
                catch
                {
                    textDto = new ExtractedTextDto(extractedText, new(), new());
                }
            }
            else
            {
                textDto = new ExtractedTextDto(extractedText, new(), new());
            }

            return Result.Success(textDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while retrieving extracted text. DocumentId: {DocumentId}", documentId);
            return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}

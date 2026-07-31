using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            var extension = WorkspaceDocumentHelper.NormalizeExtension(System.IO.Path.GetExtension(request.File.FileName));
            if (!WorkspaceDocumentHelper.IsSupportedUploadExtension(extension))
            {
                var allowed = string.Join(", ", WorkspaceDocumentConstants.SupportedUploadExtensions);
                return Result.Failure<WorkspaceDocumentDto>($"Unsupported file type. Allowed file types are: {allowed}.", ErrorCodes.ValidationError);
            }

            var storageKey = WorkspaceDocumentHelper.GenerateStorageKey(workspaceId, docId, extension);

            var status = isOwnerOrAdmin 
                ? WorkspaceDocumentStatus.@public 
                : WorkspaceDocumentStatus.pending_approval;

            var effectiveIsAiAllowed = request.IsAiAllowed && WorkspaceDocumentHelper.IsAiReadableExtension(extension);

            WorkspaceDocumentIngestionStatus ingestionStatus;
            if (!effectiveIsAiAllowed)
            {
                ingestionStatus = WorkspaceDocumentIngestionStatus.skipped;
            }
            else
            {
                ingestionStatus = isOwnerOrAdmin 
                    ? WorkspaceDocumentIngestionStatus.pending 
                    : WorkspaceDocumentIngestionStatus.awaiting_approval;
            }

            var aiEligible = false; // Initial state: false until ingestion completes or if IsAiAllowed == false

            var document = request.ToEntity(docId, workspaceId, userId, storageKey, _storage.StorageProviderName, status, ingestionStatus, aiEligible);
            document.IsAiAllowed = effectiveIsAiAllowed;

            // Save the document content securely to physical storage (AES-256 + HMAC-SHA512) before DB transaction
            using (var stream = request.File.OpenReadStream())
            {
                await _storage.SaveDocumentContentAsync(document, stream, ct);
            }

            try
            {
                await _unitOfWork.WorkspaceDocumentRepository.AddAsync(document, ct);
                if (isOwnerOrAdmin && effectiveIsAiAllowed)
                {
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        userId,
                        document.ConfidentialityLevel,
                        ct);
                }
                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch
            {
                // The blob is already on disk but has no DB row to reference it — clean it up
                // rather than leaving an orphaned encrypted file behind.
                await _storage.DeleteDocumentContentAsync(document, ct);
                throw;
            }

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                status == WorkspaceDocumentStatus.pending_approval
                    ? WorkspaceDocumentConstants.LifecycleEvents.PendingApproval
                    : WorkspaceDocumentConstants.LifecycleEvents.Created,
                document.UpdatedAt,
                userId,
                ct);

            await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.UploadDocument, new { document.Name, document.ConfidentialityLevel }, _logger, ct);

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
            var approvedByDocument = await _unitOfWork.WorkspaceDocumentAuditRepository.GetLatestApproverUserIdsByWorkspaceAsync(workspaceId, ct);

            var filteredDocs = documents.OrderByDescending(d => d.CreatedAt).AsEnumerable();
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
                    approvedByDocument.TryGetValue(doc.Id, out var approvedBy);
                    allowedDtos.Add(doc.ToDto(downloadUrl, approvedBy));
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
            var approvalAudit = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                a => a.DocumentId == documentId &&
                     a.Action == WorkspaceDocumentConstants.AuditActions.ApproveDocument,
                "",
                ct);
            return Result.Success(document.ToDto(downloadUrl, approvalAudit?.ActorId));
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

            if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel))
            {
                document.ConfidentialityLevel = request.ConfidentialityLevel;
            }

            if (request.IsAiAllowed.HasValue && request.IsAiAllowed.Value != document.IsAiAllowed)
            {
                var wasAllowed = document.IsAiAllowed;
                document.IsAiAllowed = request.IsAiAllowed.Value;

                if (!document.IsAiAllowed)
                {
                    // Toggled to Administrative Document (IsAiAllowed = false)
                    document.AiEligible = false;
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                    
                    // Invalidate and delete existing vectors in Qdrant Vector DB
                    await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
                }
                else
                {
                    // Toggled back to AI Context Document (IsAiAllowed = true)
                    document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
                    if (string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        await _eventPublisher.PublishDocumentUploadedAsync(
                            document.Id,
                            workspaceId,
                            document.StorageKey,
                            document.FileName,
                            document.FileExtension,
                            userId,
                            document.ConfidentialityLevel,
                            ct);
                    }
                }
            }

            document.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _unitOfWork.SaveChangesAsync(ct);
            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Updated,
                document.UpdatedAt,
                userId,
                ct);

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
                return Result.Failure("Forbidden. Only workspace Owner/Admin or the document owner can manage document access policies.", ErrorCodes.Forbidden);
            }

            var normalizedSubjectType = WorkspaceDocumentHelper.NormalizePolicySubjectType(request.SubjectType);
            var normalizedPermission = request.Permission?.Trim().ToLowerInvariant();
            var normalizedEffect = request.Effect?.Trim().ToUpperInvariant();
            var normalizedSubjectKey = request.SubjectKey?.Trim();

            if (normalizedSubjectType == null)
            {
                return Result.Failure("SubjectType must be User, Role, or MembershipType.", ErrorCodes.ValidationError);
            }
            if (!WorkspaceDocumentHelper.IsSupportedPolicyPermission(normalizedPermission))
            {
                return Result.Failure("Permission must be view, download, or ai_retrieval.", ErrorCodes.ValidationError);
            }
            if (normalizedEffect is not WorkspacePolicyConstants.EffectAllow and not WorkspacePolicyConstants.EffectDeny)
            {
                return Result.Failure("Effect must be ALLOW or DENY.", ErrorCodes.ValidationError);
            }

            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeUser && !request.SubjectId.HasValue)
            {
                return Result.Failure("SubjectId is required for a User policy.", ErrorCodes.ValidationError);
            }
            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeRole
                && (normalizedSubjectKey == null
                    || (!normalizedSubjectKey.IsOwner()
                        && !normalizedSubjectKey.IsAdmin()
                        && !normalizedSubjectKey.IsMember())))
            {
                return Result.Failure("Role policy SubjectKey must be Owner, Admin, or Member.", ErrorCodes.ValidationError);
            }
            if (normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeMembershipType
                && !Enum.TryParse<MembershipType>(normalizedSubjectKey, true, out _))
            {
                return Result.Failure("MembershipType policy SubjectKey must be Internal or External.", ErrorCodes.ValidationError);
            }

            normalizedSubjectKey = normalizedSubjectType switch
            {
                WorkspacePolicyConstants.SubjectTypeRole => normalizedSubjectKey!.ToWorkspaceMemberRole().ToRoleName(),
                WorkspacePolicyConstants.SubjectTypeMembershipType => Enum.Parse<MembershipType>(normalizedSubjectKey!, true).ToString(),
                _ => null
            };
            var normalizedSubjectId = normalizedSubjectType == WorkspacePolicyConstants.SubjectTypeUser
                ? request.SubjectId
                : null;

            var existingPolicy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FirstOrDefaultAsync(
                policy => policy.DocumentId == documentId
                          && policy.WorkspaceId == workspaceId
                          && policy.SubjectType == normalizedSubjectType
                          && policy.SubjectId == normalizedSubjectId
                          && policy.SubjectKey == normalizedSubjectKey
                          && policy.Permission == normalizedPermission,
                "",
                ct);
            if (existingPolicy != null)
            {
                return Result.Failure(
                    "A policy already exists for this subject and permission. Remove it before changing the effect.",
                    ErrorCodes.Conflict);
            }

            var normalizedRequest = request with
            {
                SubjectType = normalizedSubjectType,
                SubjectId = normalizedSubjectId,
                SubjectKey = normalizedSubjectKey,
                Permission = normalizedPermission,
                Effect = normalizedEffect
            };
            var policy = normalizedRequest.ToEntity(documentId, workspaceId, userId);

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
                return Result.Failure("Forbidden. Only workspace Owner/Admin or the document owner can manage document access policies.", ErrorCodes.Forbidden);
            }

            var policy = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository.GetByIdAsync(policyId, ct);
            if (policy == null || policy.DocumentId != documentId || policy.WorkspaceId != workspaceId)
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
                return Result.Failure<PagedResult<WorkspaceDocumentAccessPolicyDto>>("Forbidden. Only workspace Owner/Admin or the document owner can view access policies.", ErrorCodes.Forbidden);
            }

            var (policies, totalCount) = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository
                .GetPagedAccessPoliciesAsync(documentId, query.Page, query.PageSize, isDescending: true, ct);

            var dtos = policies.Select(p => p.ToDto()).ToList();
            var pagedResult = new PagedResult<WorkspaceDocumentAccessPolicyDto>(dtos, query.Page, query.PageSize, totalCount);

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
                document.Status = WorkspaceDocumentStatus.@public.ToString();
                document.AiEligible = false;
                document.IngestionStatus = document.IsAiAllowed
                    ? WorkspaceDocumentIngestionStatus.pending.ToString() 
                    : WorkspaceDocumentIngestionStatus.skipped.ToString();
                document.UpdatedAt = DateTime.UtcNow;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                if (document.IsAiAllowed)
                {
                    await _eventPublisher.PublishDocumentUploadedAsync(
                        document.Id,
                        workspaceId,
                        document.StorageKey,
                        document.FileName,
                        document.FileExtension,
                        document.UploadedBy ?? userId,
                        document.ConfidentialityLevel,
                        ct);
                }
                await _unitOfWork.SaveChangesAsync(ct);
                await _eventPublisher.PublishDocumentLifecycleAsync(
                    document.Id,
                    workspaceId,
                    document.Status,
                    document.IngestionStatus,
                    WorkspaceDocumentConstants.LifecycleEvents.Approved,
                    document.UpdatedAt,
                    userId,
                    ct);

                await _unitOfWork.AuditAsync(document.Id, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.ApproveDocument, logger: _logger, ct: ct);
            }
            else
            {
                document.Status = WorkspaceDocumentStatus.rejected.ToString();
                document.AiEligible = false;
                document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
                document.UpdatedAt = DateTime.UtcNow;

                _unitOfWork.WorkspaceDocumentRepository.Update(document);
                await _unitOfWork.SaveChangesAsync(ct);
                await _eventPublisher.PublishDocumentLifecycleAsync(
                    document.Id,
                    workspaceId,
                    document.Status,
                    document.IngestionStatus,
                    WorkspaceDocumentConstants.LifecycleEvents.Rejected,
                    document.UpdatedAt,
                    userId,
                    ct);

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

    public async Task<Result<DocumentDownloadStreamDto>> DownloadDocumentAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var accessResult = await _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download, ct);
            if (!accessResult.IsSuccess)
            {
                return Result.Failure<DocumentDownloadStreamDto>(accessResult.Error ?? "Access denied.", ErrorCodes.Forbidden);
            }

            var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
            if (document == null)
            {
                return Result.Failure<DocumentDownloadStreamDto>("Document not found.", ErrorCodes.NotFound);
            }

            var stream = await _storage.GetDecryptedStreamAsync(document, ct);

            await _unitOfWork.AuditAsync(documentId, workspaceId, userId, WorkspaceDocumentConstants.AuditActions.DownloadDocument, logger: _logger, ct: ct);

            return Result.Success(new DocumentDownloadStreamDto(stream, WorkspaceDocumentHelper.GetSafeContentType(document.FileExtension), document.FileName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while downloading document. DocumentId: {DocumentId}", documentId);
            return Result.Failure<DocumentDownloadStreamDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
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
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _eventPublisher.PublishDocumentDeletedAsync(documentId, workspaceId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                "deleted",
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Deleted,
                document.UpdatedAt,
                userId,
                ct);

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
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            await _eventPublisher.PublishDocumentArchivedAsync(documentId, workspaceId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Archived,
                document.UpdatedAt,
                userId,
                ct);

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
                var audit = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                    a => a.DocumentId == document.Id && a.Action == WorkspaceDocumentConstants.AuditActions.ArchiveDocument, "", ct);
                var isArchiver = audit != null && audit.ActorId == userId;
                if (!isArchiver)
                {
                    return Result.Failure("Forbidden. Only owner, admin, document owner, or archiver can restore.", ErrorCodes.Forbidden);
                }
            }

            if (document.Status != WorkspaceDocumentStatus.archived.ToString())
            {
                return Result.Failure("Document is not archived.", ErrorCodes.ValidationError);
            }

            document.Status = WorkspaceDocumentStatus.@public.ToString();
            document.IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString();
            document.AiEligible = false; // Scanner will re-evaluate on background security scan
            document.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.WorkspaceDocumentRepository.Update(document);
            if (document.IsAiAllowed)
            {
                await _eventPublisher.PublishDocumentUploadedAsync(
                    document.Id,
                    workspaceId,
                    document.StorageKey,
                    document.FileName,
                    document.FileExtension,
                    document.UploadedBy ?? userId,
                    document.ConfidentialityLevel,
                    ct);
            }
            await _unitOfWork.SaveChangesAsync(ct);
            await _eventPublisher.PublishDocumentLifecycleAsync(
                document.Id,
                workspaceId,
                document.Status,
                document.IngestionStatus,
                WorkspaceDocumentConstants.LifecycleEvents.Restored,
                document.UpdatedAt,
                userId,
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

            string extractedText = string.Empty;
            try
            {
                extractedText = await _storage.GetExtractedTextAsync(document, ct);
                if (string.IsNullOrEmpty(extractedText))
                {
                    ExtractedDocumentContent content;
                    using (var decryptedStream = await _storage.GetDecryptedStreamAsync(document, ct))
                    {
                        content = await _textExtractor.ExtractTextAsync(decryptedStream, document.FileExtension, ct);
                    }
                    extractedText = JsonSerializer.Serialize(content);
                    await _storage.SaveExtractedTextAsync(document, extractedText, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not extract or load storage file for DocumentId: {DocumentId}", documentId);
                extractedText = string.Empty;
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

    public async Task<Result<ExtractedTextDto>> UpdateExtractedTextAsync(Guid workspaceId, Guid documentId, string text, Guid userId, CancellationToken ct = default)
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

            var content = new ExtractedDocumentContent { FullText = text };
            var jsonContent = JsonSerializer.Serialize(content);
            await _storage.SaveExtractedTextAsync(document, jsonContent, ct);

            if (document.IsAiAllowed &&
                string.Equals(
                    document.Status,
                    WorkspaceDocumentStatus.@public.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                await _eventPublisher.PublishEmbeddingIndexRequestAsync(document.Id, document.WorkspaceId, text, true, ct);
            }

            var textDto = new ExtractedTextDto(text, new(), new());
            return Result.Success(textDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating extracted text. DocumentId: {DocumentId}", documentId);
            return Result.Failure<ExtractedTextDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}

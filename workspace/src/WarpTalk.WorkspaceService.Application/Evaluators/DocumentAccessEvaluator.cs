using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Application.Evaluators;

public class DocumentAccessEvaluator : IDocumentAccessEvaluator
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentAccessEvaluator> _logger;

    public DocumentAccessEvaluator(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient,
        IConfiguration configuration,
        ILogger<DocumentAccessEvaluator> logger)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Result> EvaluateAccessAsync(Guid userId, Guid workspaceId, Guid documentId, string requiredPermission, CancellationToken ct = default)
    {
        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
        {
            return Result.Failure(WorkspaceConstants.Errors.DocumentNotFound);
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.AccessDeniedNotMember);
        }

        var roleName = WorkspaceMemberRole.Member.ToRoleName();
        try
        {
            var role = await _authIdentity.GetRoleByIdAsync(member.RoleId, ct);
            if (role != null)
            {
                roleName = role.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch role from identity service for RoleId: {RoleId} in workspace {WorkspaceId}. Falling back to default role: {DefaultRole}", member.RoleId, workspaceId, WorkspaceMemberRole.Member.ToRoleName());
        }

        var policies = await _unitOfWork.WorkspaceDocumentAccessPolicyRepository
            .FindAsync(p => p.DocumentId == documentId, "", ct);

        return await EvaluateAccessAsync(userId, workspaceId, document, requiredPermission, member, roleName, policies, null, null, ct);
    }

    public async Task<Result> EvaluateAccessAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        string requiredPermission,
        WorkspaceMember member,
        string roleName,
        IEnumerable<WorkspaceDocumentAccessPolicy> policies,
        Dictionary<Guid, TranslationRoomDto?>? roomCache = null,
        Dictionary<Guid, List<TranslationRoomParticipantDto>>? participantsCache = null,
        CancellationToken ct = default)
    {
        var isPublishedDocument = IsPublishedDocumentStatus(document.Status);

        // Archived check: only Owner/Admin, Document Owner, or the Archiver can view/download archived documents.
        if (string.Equals(document.Status, WorkspaceDocumentStatus.archived.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;
            if (!isOwnerOrAdmin && !isDocOwner)
            {
                var audit = await _unitOfWork.WorkspaceDocumentAuditRepository.FirstOrDefaultAsync(
                    a => a.DocumentId == document.Id && a.Action == WorkspaceDocumentConstants.AuditActions.ArchiveDocument, "", ct);
                var isArchiver = audit != null && audit.ActorId == userId;
                if (!isArchiver)
                {
                    return Result.Failure(WorkspaceConstants.Errors.AccessDeniedDefault);
                }
            }
        }

        if (IsApprovalRestrictedStatus(document.Status))
        {
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;
            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure(WorkspaceConstants.Errors.AccessDeniedDefault);
            }
        }

        if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.Download, StringComparison.OrdinalIgnoreCase))
        {
            if (!isPublishedDocument)
            {
                var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
                var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;
                if (!isOwnerOrAdmin && !isDocOwner)
                {
                    return Result.Failure(WorkspaceConstants.Errors.AccessDeniedDefault);
                }
            }
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.AiRetrieval, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.IngestionStatus, WorkspaceDocumentIngestionStatus.completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
                document.LastIndexedAt == null ||
                !document.AiEligible)
            {
                return Result.Failure("Document is not eligible for AI retrieval.");
            }
        }

        // 1. AI Ingestion Status check (Security-first)
        if (string.Equals(document.IngestionStatus, WorkspaceDocumentIngestionStatus.pending.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(document.IngestionStatus, WorkspaceDocumentIngestionStatus.awaiting_approval.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            var isDocOwner = document.OwnerId == userId || document.UploadedBy == userId;
            if (!isOwnerOrAdmin && !isDocOwner)
            {
                return Result.Failure(WorkspaceConstants.Errors.AccessDeniedPendingIngestion);
            }
        }

        // 2. Evaluate explicit policies with hierarchy propagation
        var subjectPolicies = policies.Where(p =>
            (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeUser, StringComparison.OrdinalIgnoreCase) && p.SubjectId == userId) ||
            (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeRole, StringComparison.OrdinalIgnoreCase) && string.Equals(p.SubjectKey, roleName, StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeMembershipType, StringComparison.OrdinalIgnoreCase) && string.Equals(p.SubjectKey, member.MembershipType, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        // Build sets of denied and allowed permissions for this subject
        var deniedPermissions = subjectPolicies
            .Where(p => string.Equals(p.Effect, WorkspacePolicyConstants.EffectDeny, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Permission.ToLowerInvariant())
            .ToHashSet();

        var allowedPermissions = subjectPolicies
            .Where(p => string.Equals(p.Effect, WorkspacePolicyConstants.EffectAllow, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Permission.ToLowerInvariant())
            .ToHashSet();

        // 2a. Determine if denied by hierarchical rules
        bool isDenied = false;
        if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.View, StringComparison.OrdinalIgnoreCase))
        {
            isDenied = deniedPermissions.Contains("view");
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.Download, StringComparison.OrdinalIgnoreCase))
        {
            isDenied = deniedPermissions.Contains("download") || deniedPermissions.Contains("view");
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.AiRetrieval, StringComparison.OrdinalIgnoreCase))
        {
            isDenied = deniedPermissions.Contains("ai_retrieval") || deniedPermissions.Contains("view");
        }
        else
        {
            isDenied = deniedPermissions.Contains(requiredPermission.ToLowerInvariant());
        }

        if (isDenied)
        {
            return Result.Failure(WorkspaceConstants.Errors.AccessDeniedByPolicy);
        }

        // 2b. Determine if allowed by hierarchical rules
        bool isAllowed = false;
        if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.View, StringComparison.OrdinalIgnoreCase))
        {
            isAllowed = allowedPermissions.Contains("view") || allowedPermissions.Contains("download") || allowedPermissions.Contains("ai_retrieval");
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.Download, StringComparison.OrdinalIgnoreCase))
        {
            isAllowed = allowedPermissions.Contains("download");
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.AiRetrieval, StringComparison.OrdinalIgnoreCase))
        {
            isAllowed = allowedPermissions.Contains("ai_retrieval");
        }
        else
        {
            isAllowed = allowedPermissions.Contains(requiredPermission.ToLowerInvariant());
        }

        if (isAllowed)
        {
            return Result.Success();
        }

        // 3. Fallback to default action
        if (document.IsRestricted())
        {
            return Result.Failure(WorkspaceConstants.Errors.AccessDeniedSensitive);
        }

        // Non-sensitive default action
        if (string.Equals(member.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            // Internal users can access non-sensitive documents by default
            return Result.Success();
        }

        if (string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            // Check meeting exception
            if (string.Equals(document.SourceType, WorkspaceDocumentConstants.SourceTypeMeeting, StringComparison.OrdinalIgnoreCase) && document.SourceId.HasValue)
            {
                TranslationRoomDto? room = null;
                if (roomCache != null && roomCache.TryGetValue(document.SourceId.Value, out var cachedRoom))
                {
                    room = cachedRoom;
                }
                else
                {
                    room = await _translationRoomClient.GetTranslationRoomAsync(document.SourceId.Value, ct);
                }

                if (room != null)
                {
                    List<TranslationRoomParticipantDto>? participants = null;
                    if (participantsCache != null && participantsCache.TryGetValue(document.SourceId.Value, out var cachedParticipants))
                    {
                        participants = cachedParticipants;
                    }
                    else
                    {
                        participants = await _translationRoomClient.GetParticipantsAsync(document.SourceId.Value, ct);
                    }

                    var isParticipant = participants != null && participants.Any(p => p.Id == userId);
                    if (isParticipant)
                    {
                        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
                        var config = workspace != null ? WorkspaceHelper.GetWorkspaceConfig(workspace) : new WorkspaceConfiguration();
                        var gracePeriodHours = config.ExternalGracePeriodHours 
                            ?? _configuration.GetValue<int>(WorkspaceConstants.DefaultExternalGracePeriodHoursKey, 24);

                        var isWithinGracePeriod = true;
                        if (room.EndedAt.HasValue)
                        {
                            isWithinGracePeriod = (DateTime.UtcNow - room.EndedAt.Value).TotalHours <= gracePeriodHours;
                        }

                        if (isWithinGracePeriod)
                        {
                            return Result.Success();
                        }
                    }
                }
            }
        }

        return Result.Failure(WorkspaceConstants.Errors.AccessDeniedDefault);
    }

    public async Task<bool> CanManagePoliciesAsync(Guid userId, Guid workspaceId, Guid documentId, CancellationToken ct = default)
    {
        var document = await _unitOfWork.WorkspaceDocumentRepository.GetByIdAsync(documentId, ct);
        if (document == null || document.WorkspaceId != workspaceId || document.DeletedAt != null)
        {
            return false;
        }

        if (document.OwnerId == userId)
        {
            return true;
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
        if (member == null)
        {
            return false;
        }

        var roleName = WorkspaceMemberRole.Member.ToRoleName();
        try
        {
            var role = await _authIdentity.GetRoleByIdAsync(member.RoleId, ct);
            if (role != null)
            {
                roleName = role.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch role from identity service for RoleId: {RoleId} in workspace {WorkspaceId}. Falling back to default role: {DefaultRole}", member.RoleId, workspaceId, WorkspaceMemberRole.Member.ToRoleName());
        }

        // Strictly Workspace Owner or Admin only (excluding regular uploaders)
        return roleName.IsOwnerOrAdmin();
    }

    private static bool IsPublishedDocumentStatus(string? status)
    {
        return string.Equals(status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovalRestrictedStatus(string? status)
    {
        return string.Equals(status, WorkspaceDocumentStatus.pending_approval.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, WorkspaceDocumentStatus.rejected.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

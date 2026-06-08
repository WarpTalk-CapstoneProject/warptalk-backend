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

        return await EvaluateAccessAsync(userId, workspaceId, document, requiredPermission, member, roleName, policies, ct);
    }

    public async Task<Result> EvaluateAccessAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        string requiredPermission,
        WorkspaceMember member,
        string roleName,
        IEnumerable<WorkspaceDocumentAccessPolicy> policies,
        CancellationToken ct = default)
    {
        if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.Download, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(document.Status, WorkspaceDocumentStatus.active.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(WorkspaceConstants.Errors.AccessDeniedDefault);
            }
        }
        else if (string.Equals(requiredPermission, WorkspaceDocumentPermissions.AiRetrieval, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(document.Status, WorkspaceDocumentStatus.active.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.IngestionStatus, WorkspaceDocumentIngestionStatus.completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
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

        // 2. Evaluate explicit policies
        var matchingPolicies = policies.Where(p =>
            string.Equals(p.Permission, requiredPermission, StringComparison.OrdinalIgnoreCase) &&
            (
                (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeUser, StringComparison.OrdinalIgnoreCase) && p.SubjectId == userId) ||
                (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeRole, StringComparison.OrdinalIgnoreCase) && string.Equals(p.SubjectKey, roleName, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(p.SubjectType, WorkspacePolicyConstants.SubjectTypeMembershipType, StringComparison.OrdinalIgnoreCase) && string.Equals(p.SubjectKey, member.MembershipType, StringComparison.OrdinalIgnoreCase))
            )
        ).ToList();

        // Deny overrides: if any policy is DENY, block access
        if (matchingPolicies.Any(p => string.Equals(p.Effect, WorkspacePolicyConstants.EffectDeny, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure(WorkspaceConstants.Errors.AccessDeniedByPolicy);
        }

        // If no DENY, but ALLOW exists, grant access
        if (matchingPolicies.Any(p => string.Equals(p.Effect, WorkspacePolicyConstants.EffectAllow, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Success();
        }

        // 3. Fallback to default action
        if (document.IsSensitive)
        {
            return Result.Failure(WorkspaceConstants.Errors.AccessDeniedSensitive);
        }

        // Non-sensitive default action
        if (string.Equals(member.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        if (string.Equals(member.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            // Check meeting exception
            if (string.Equals(document.SourceType, WorkspaceDocumentConstants.SourceTypeMeeting, StringComparison.OrdinalIgnoreCase) && document.SourceId.HasValue)
            {
                var room = await _translationRoomClient.GetTranslationRoomAsync(document.SourceId.Value, ct);
                if (room != null)
                {
                    var participants = await _translationRoomClient.GetParticipantsAsync(document.SourceId.Value, ct);
                    var isParticipant = participants.Any(p => p.Id == userId);
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

        if (roleName.IsOwnerOrAdmin())
        {
            return true;
        }

        if (document.OwnerId == userId || document.UploadedBy == userId)
        {
            return true;
        }

        return false;
    }
}

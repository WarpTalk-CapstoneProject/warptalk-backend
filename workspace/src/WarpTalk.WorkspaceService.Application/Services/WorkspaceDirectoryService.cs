using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceDirectoryService : IWorkspaceDirectoryService
{
    private const string DefaultRoleName = "Member";
    private const string DefaultMembershipType = "internal";
    private const string ActiveMemberStatus = "active";
    private const string VerifiedDomainStatus = "verified";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;

    public WorkspaceDirectoryService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
    }

    public async Task<Result<WorkspaceMemberDetailsDto?>> GetMemberDetailsAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        var member = await FindActiveMembershipAsync(workspaceId, userId, ct);
        if (member == null)
            return Result.Success<WorkspaceMemberDetailsDto?>(null);

        var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);

        return Result.Success<WorkspaceMemberDetailsDto?>(new WorkspaceMemberDetailsDto(
            roleName ?? DefaultRoleName,
            member.MembershipType ?? DefaultMembershipType,
            string.Equals(member.Status, ActiveMemberStatus, StringComparison.OrdinalIgnoreCase),
            member.CanCreateMeetings));
    }

    public async Task<Result<IReadOnlyList<WorkspaceNameDto>>> GetWorkspaceNamesAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default)
    {
        if (workspaceIds.Count == 0)
            return Result.Success<IReadOnlyList<WorkspaceNameDto>>(Array.Empty<WorkspaceNameDto>());

        var workspaces = await _unitOfWork.WorkspaceRepository.FindAsync(
            workspace => workspaceIds.Contains(workspace.Id), "", ct);

        var names = workspaces
            .Select(workspace => new WorkspaceNameDto(workspace.Id, workspace.Name))
            .ToList();

        return Result.Success<IReadOnlyList<WorkspaceNameDto>>(names);
    }

    public async Task<Result<MeetingCreationDecisionDto>> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IReadOnlyCollection<string> targetLanguages,
        CancellationToken ct = default)
    {
        var member = await FindActiveMembershipAsync(workspaceId, userId, ct);
        if (member == null)
            return Decision(MeetingCreationDecisionDto.Denied("User is not a member of this workspace."));

        if (!string.Equals(member.Status, ActiveMemberStatus, StringComparison.OrdinalIgnoreCase))
            return Decision(MeetingCreationDecisionDto.Denied("Workspace member is inactive."));

        if (!member.CanCreateMeetings)
            return Decision(MeetingCreationDecisionDto.Denied("User does not have permission to create meetings."));

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
            return Decision(MeetingCreationDecisionDto.Denied("Workspace not found."));

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        if (targetLanguages.Count > 0
            && config.AllowedTargetLanguages != null
            && config.AllowedTargetLanguages.Any())
        {
            var unsupported = targetLanguages.FirstOrDefault(lang =>
                !config.AllowedTargetLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase));
            if (unsupported != null)
            {
                return Decision(MeetingCreationDecisionDto.Denied(
                    $"Target language '{unsupported}' is not allowed by the workspace policy."));
            }
        }

        var activeRoomCount = await _translationRoomClient.GetActiveRoomCountAsync(workspaceId, ct);
        if (config.MaxActiveRooms > 0 && activeRoomCount >= config.MaxActiveRooms)
        {
            return Decision(MeetingCreationDecisionDto.Denied(
                $"Workspace active room limit ({config.MaxActiveRooms}) has been reached."));
        }

        return Decision(MeetingCreationDecisionDto.Allowed());
    }

    public async Task<Result<WorkspaceSettingsSnapshotDto>> GetSettingsAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
            return Result.Failure<WorkspaceSettingsSnapshotDto>("Workspace not found.", ErrorCodes.NotFound);

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        return Result.Success(new WorkspaceSettingsSnapshotDto(
            config.ArtifactRetentionDays,
            config.AllowExternalCollaboration,
            config.IsProfanityFilterEnabled,
            // Opt-out semantics: unset at workspace level ⇒ allowed. Mirrors the fallback
            // DocumentSecurityGuardrailConsumerService.ResolvePolicySettingsAsync already
            // applies for documents.
            config.AiUsagePolicy?.AllowExternalLlm ?? true,
            config.AiUsagePolicy?.UseGlobalGlossary ?? true));
    }

    public async Task<Result<WorkspacePreflightDto>> GetPreflightAsync(
        Guid workspaceId,
        string? userEmail,
        CancellationToken ct = default)
    {
        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
            return Result.Failure<WorkspacePreflightDto>("Workspace not found.", ErrorCodes.NotFound);

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        var isDomainMatched = false;
        if (!string.IsNullOrWhiteSpace(userEmail)
            && EmailAddress.TryParse(userEmail, out var emailAddress)
            && emailAddress != null)
        {
            var domain = emailAddress.Domain;
            isDomainMatched = await _unitOfWork.WorkspaceVerifiedDomainRepository.AnyAsync(
                vd => vd.WorkspaceId == workspaceId
                      && vd.Domain.ToLower() == domain.ToLower()
                      && vd.Status == VerifiedDomainStatus
                      && vd.VerifiedAt != null
                      && vd.RevokedAt == null,
                ct);
        }

        return Result.Success(new WorkspacePreflightDto(
            workspace.IsActive && workspace.DeletedAt == null,
            workspace.Name,
            workspace.Slug,
            isDomainMatched,
            config.AllowExternalCollaboration));
    }

    private Task<Domain.Entities.WorkspaceMember?> FindActiveMembershipAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct) =>
        _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

    // A denied decision is still a successfully computed answer — the caller needs the
    // reason string, not a failed Result.
    private static Result<MeetingCreationDecisionDto> Decision(MeetingCreationDecisionDto decision) =>
        Result.Success(decision);
}

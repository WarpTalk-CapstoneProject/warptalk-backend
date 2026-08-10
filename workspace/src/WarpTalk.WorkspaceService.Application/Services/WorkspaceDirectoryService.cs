using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs;
using WarpTalk.WorkspaceService.Application.Entitlements;
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

    /// <summary>
    /// The one denial reason every tenant-lifecycle gate returns. Worded for the person who hits
    /// it, not the admin who caused it: the suspension reason is audit data and is deliberately
    /// not leaked to members.
    /// </summary>
    internal const string WorkspaceSuspendedMessage =
        "This workspace is suspended. Contact your administrator to restore it.";

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

        // A suspended tenant may not open new meetings. Every other rule below is about WHO is
        // asking or WHAT they asked for; this one is about whether the workspace is entitled to
        // spend anything at all, so it is answered before any of them. Its absence is the whole
        // reason suspending a workspace used to stop document upload and new invitations while
        // leaving meetings — and the billable AI usage they drive — running.
        if (!IsLiveTenant(workspace))
            return Decision(MeetingCreationDecisionDto.Denied(WorkspaceSuspendedMessage));

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

        // WT-263: ONE local read serves every plan-derived limit below. No network call to
        // BillingService is made from here on, which is why a BillingService outage can no longer
        // affect meeting creation. WT-239 moved the read here with the rest of the decision; the
        // gRPC boundary no longer touches the unit of work.
        var snapshot = await _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, ct);
        var entitlements = snapshot == null
            ? WorkspaceEntitlements.Unknown
            : WorkspaceEntitlements.FromSnapshot(snapshot.EntitlementsJson, snapshot.HasActiveSubscription);

        var activeRoomLimit = ResolveMaxActiveRooms(entitlements, config);
        if (activeRoomLimit > 0)
        {
            var activeRoomCount = await _translationRoomClient.GetActiveRoomCountAsync(workspaceId, ct);
            if (activeRoomCount >= activeRoomLimit)
            {
                return Decision(MeetingCreationDecisionDto.Denied(
                    $"Workspace active room limit ({activeRoomLimit}) has been reached."));
            }
        }

        var planLanguageDenial = ValidatePlanLanguageQuota(entitlements, targetLanguages.Count);
        if (planLanguageDenial != null)
        {
            return Decision(planLanguageDenial);
        }

        return Decision(MeetingCreationDecisionDto.Allowed());
    }

    /// <summary>
    /// WT-263: enforces <c>max_languages</c> from the LOCAL entitlement snapshot. Returns null when
    /// the request clears the quota, or a denial when it does not.
    ///
    /// Synchronous and non-async, because there is nothing left to await. WT-262 phase 1 called
    /// BillingService here and had to fail closed on an outage — it could not tell "unknown" from
    /// "allowed", and a quota with no after-the-fact remedy cannot be handed out on a guess. That
    /// call, its fail-closed branch, and the single-target-language carve-out that bounded its blast
    /// radius are all deleted: the value is replicated ahead of time, so the question is answered
    /// from this service's own database and BillingService's availability never enters into it.
    ///
    /// What is NOT changed: the workspace-permission gate above still fails closed (WT-249). That
    /// one is a permission decision with no local replica, and it must stay that way.
    ///
    /// A null limit means the quota is not in force — cold start, or no live subscription. Neither
    /// is a denial; the workspace's own AllowedTargetLanguages policy governs those cases, exactly
    /// as it did before WT-262. See WorkspaceEntitlements for the cold-start reasoning.
    /// </summary>
    private static MeetingCreationDecisionDto? ValidatePlanLanguageQuota(
        WorkspaceEntitlements entitlements,
        int requestedLanguageCount)
    {
        if (requestedLanguageCount <= 0)
        {
            return null;
        }

        var maxLanguages = entitlements.Limit(EntitlementKeys.MaxLanguages);
        if (maxLanguages is null or <= 0)
        {
            return null;
        }

        if (requestedLanguageCount > maxLanguages.Value)
        {
            return MeetingCreationDecisionDto.Denied(
                $"Your plan allows {maxLanguages.Value} target language(s) per meeting; {requestedLanguageCount} were requested.");
        }

        return null;
    }

    /// <summary>
    /// WT-263: <c>max_active_rooms</c> is now an ordinary entitlement key — no sentinel, no special
    /// case.
    ///
    /// The old design called for a <c>-1</c> "inherit from plan" sentinel in the settings JSON.
    /// Provenance replaces it: an owner-set value arrives resolved with source
    /// <c>workspace_override</c>, an unset one resolves from the plan, and neither the caller nor
    /// the storage needs a magic number to tell them apart. The resolver has already clamped an
    /// owner's value to the plan ceiling, so whatever arrives here is enforceable as-is.
    ///
    /// The settings-JSON value remains the fallback for cold start only. It is where every existing
    /// workspace's number lives today, and dropping straight to it keeps behaviour identical for a
    /// workspace whose snapshot has not arrived — the same rule the pre-WT-263 code applied.
    /// </summary>
    private static int ResolveMaxActiveRooms(
        WorkspaceEntitlements entitlements,
        Domain.Settings.WorkspaceConfiguration config)
    {
        var resolved = entitlements.SelfServiceLimit(EntitlementKeys.MaxActiveRooms);
        if (resolved.HasValue)
        {
            return (int)Math.Clamp(resolved.Value, int.MinValue, int.MaxValue);
        }

        return config.MaxActiveRooms;
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
            config.AiUsagePolicy?.UseGlobalGlossary ?? true,
            config.EnforceHostApprovalDefault));
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
            IsLiveTenant(workspace),
            workspace.Name,
            workspace.Slug,
            isDomainMatched,
            config.AllowExternalCollaboration));
    }

    /// <summary>
    /// A workspace is a live tenant only while it is active and not soft-deleted.
    ///
    /// Suspension flips <c>is_active</c> and nothing else (AdminWorkspaceService.ChangeLifecycleAsync)
    /// — no data is removed, no event is published — so this pair IS the tenant kill switch, and
    /// reading it is the only way any caller can observe a suspension. It lives in one place so the
    /// next gate that needs it copies a call rather than a comparison; the comparison had already
    /// been written out longhand four times (WorkspaceDocumentService twice,
    /// WorkspaceInvitationService, and the preflight below) and missed in the one place that
    /// governs spend.
    ///
    /// Deliberately NOT folded into <see cref="GetMemberDetailsAsync"/>'s <c>IsActive</c>: that flag
    /// is the MEMBER's status and its consumers — BillingService's WorkspaceAuthorizationService,
    /// the Gateway's RoomHostAuthority, TranslationRoomService's WorkspaceMemberGrpcDirectory — read
    /// it to mean "this person is a live member". Overloading it would, among other things, lock a
    /// suspended workspace's owner out of the billing pages they need to get reinstated.
    /// </summary>
    private static bool IsLiveTenant(Domain.Entities.Workspace workspace) =>
        workspace.IsActive && workspace.DeletedAt == null;

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

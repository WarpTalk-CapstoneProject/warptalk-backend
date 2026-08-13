using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.Entitlements;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Application.Validators;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
using WarpTalk.Shared;
using MassTransit;
using WarpTalk.Shared.Events;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceEventPublisher _eventPublisher;

    /// <summary>
    /// WT-263: used only to PUSH the owner's self-service entitlement settings to the resolver.
    /// Optional so existing construction sites keep working; a null client simply means the
    /// workspace's settings JSON stays the only record, which is the pre-WT-263 behaviour.
    /// </summary>
    private readonly IBillingSubscriptionClient? _billingSubscriptionClient;

    public WorkspaceService(
        IUnitOfWork unitOfWork,
        IWorkspaceCacheService workspaceCache,
        ILogger<WorkspaceService> logger,
        IAuthIdentityClient authIdentity,
        IWorkspaceEventPublisher eventPublisher,
        IBillingSubscriptionClient? billingSubscriptionClient = null)
    {
        _unitOfWork = unitOfWork;
        _workspaceCache = workspaceCache;
        _logger = logger;
        _authIdentity = authIdentity;
        _eventPublisher = eventPublisher;
        _billingSubscriptionClient = billingSubscriptionClient;
    }




    public async Task<Result<WorkspaceDto>> CreateWorkspaceAsync(CreateWorkspaceRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNameRequired, ErrorCodes.ValidationError);
            }

            var user = await _authIdentity.GetUserByIdAsync(userId, ct);
            if (user == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UserNotFound, ErrorCodes.UserNotFound);
            }

            if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.InvalidUserEmail, ErrorCodes.ValidationError);
            }

            // One internal home per user. Unconditional: it is a property of the CALLER,
            // not of the workspace being created, so it must not sit behind a flag the
            // caller supplies in the request body (WT-142: "FE disabled states do not
            // replace backend authorization"). It costs nobody a slot to found a workspace
            // with no domain policy — IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync
            // only counts workspaces that require a verified domain.
            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, user.Email, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.ValidationError);
            }

            // ── Which membership policy is being asked for ────────────────────────
            // The flag in the request is an INTENT ("do I want to claim my email domain"),
            // not the stored value. What actually decides the policy is whether the
            // workspace ends up holding a verified domain: holding one IS
            // domain-verified membership, holding none IS manually-assigned membership.
            // So the two can never contradict each other, and the combination
            // {requireVerifiedDomainForInternal: false, verifiedDomains: ["acme.com"]}
            // needs no error of its own — the claimed domain settles it.
            var requireVerified = request.RequireVerifiedDomainForInternal ?? true;

            var domainsToVerify = (request.VerifiedDomains ?? new List<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requireVerified && domainsToVerify.Count == 0)
            {
                domainsToVerify.Add(emailAddress.Domain);
            }

            requireVerified = domainsToVerify.Count > 0;

            // A public mailbox domain can never become a verified company domain, so a
            // caller on one cannot found a workspace whose membership is decided by domain.
            //
            // This used to run unconditionally, against every caller. That was too wide:
            // the rule protects the trusted Internal tier, and a workspace claiming no
            // domain hands out no such tier. Refusing there blocked a legitimate case —
            // a small team on personal addresses who assign Internal and External by hand —
            // for no gain. The narrower rule below still refuses the case it was written
            // for, and the per-domain check further down is unconditional regardless.
            if (requireVerified && emailAddress.IsPublicDomain)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.PublicEmailDomainCannotCreateWorkspace, ErrorCodes.ValidationError);
            }

            // ── Domain claims ─────────────────────────────────────────────────────
            // Claiming a domain grants this workspace the trusted Internal tier over
            // everyone who later joins from that domain, so it is an authorization
            // decision, not a preference.
            foreach (var domain in domainsToVerify)
            {
                // A caller may only claim the domain of their own account email.
                // Without this an attacker at attacker.com could claim victimcorp.com
                // and auto-classify every victimcorp.com joiner as Internal.
                if (!string.Equals(domain, emailAddress.Domain, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.CannotVerifyUnownedDomain, ErrorCodes.Forbidden);
                }

                // Redundant while the ownership rule above holds (a public caller is
                // already refused), kept so relaxing that rule cannot silently
                // re-open public-domain verification.
                if (EmailAddress.IsPublicDomainName(domain))
                {
                    return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.CannotVerifyPublicDomain, ErrorCodes.ValidationError);
                }

                var owningWorkspaceId = await WorkspaceHelper.GetWorkspaceIdVerifyingDomainAsync(_unitOfWork, domain, ct);
                if (owningWorkspaceId.HasValue)
                {
                    return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.DomainRegisteredElsewhere, ErrorCodes.ValidationError);
                }
            }

            var baseSlug = SlugHelper.GenerateSlug(request.Name);
            var slug = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _unitOfWork.WorkspaceRepository, ct);

            var config = new WorkspaceConfiguration
            {
                VerifiedDomains = domainsToVerify,
                RequireVerifiedDomainForInternal = requireVerified
            };
            var settingsJson = JsonSerializer.Serialize(config);
            var workspace = request.ToEntity(slug, userId, settingsJson);

            var ownerRoleName = WorkspaceMemberRole.Owner.ToRoleName();
            var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(ownerRoleName, ct);
            if (!ownerRoleId.HasValue)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.RequiredOwnerRoleNotFound, ErrorCodes.ValidationError);
            }
            var workspaceMember = WorkspaceMemberMapper.CreateOwnerMember(workspace.Id, userId, ownerRoleId.Value);

            await _unitOfWork.WorkspaceRepository.AddAsync(workspace, ct);
            await _unitOfWork.WorkspaceMemberRepository.AddAsync(workspaceMember, ct);

            if (requireVerified)
            {
                foreach (var domain in domainsToVerify)
                {
                    var verifiedDomain = WorkspaceMapper.ToVerifiedDomainEntity(workspace.Id, domain, userId);
                    await _unitOfWork.WorkspaceVerifiedDomainRepository.AddAsync(verifiedDomain, ct);
                }
            }

            await _eventPublisher.PublishWorkspaceCreatedAsync(workspace.Id, workspace.Name, workspace.Slug, userId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(workspace.ToDto(WorkspaceMemberRole.Owner));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating workspace. UserId: {UserId}, WorkspaceName: {WorkspaceName}", userId, request.Name);
            return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UnexpectedErrorCreatingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceDto>>> GetWorkspacesAsync(GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var (workspaces, totalCount) = await _unitOfWork.WorkspaceRepository.GetWorkspacesForUserAsync(userId, query.Page, query.PageSize, query.Search, ct);

            var workspaceDtos = new List<WorkspaceDto>();
            foreach (var ws in workspaces)
            {
                var member = ws.WorkspaceMembers.FirstOrDefault();
                var defaultRoleName = WorkspaceMemberRole.Member.ToRoleName();
                var roleName = defaultRoleName;
                var membershipType = MembershipType.Internal.ToString();
                if (member != null)
                {
                    roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
                    membershipType = member.MembershipType;
                }

                workspaceDtos.Add(ws.ToDto(roleName, membershipType));
            }
            var pagedResult = new PagedResult<WorkspaceDto>(workspaceDtos, query.Page, query.PageSize, totalCount);
            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspaces for user. UserId: {UserId}", userId);
            return Result.Failure<PagedResult<WorkspaceDto>>(WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspaces, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDto>> GetWorkspaceByIdAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var activeMemberStatus = WorkspaceMemberStatus.Active.ToStorageValue();
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId
                     && m.UserId == userId
                     && m.RemovedAt == null
                     && m.Status.ToLower() == activeMemberStatus,
                "",
                ct
            );

            if (member == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (workspace.DeletedAt != null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (!workspace.IsActive)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceInactive, ErrorCodes.NotFound);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            return Result.Success(workspace.ToDto(roleName, member.MembershipType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace by ID. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDto>> GetWorkspaceByIdForAdminAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            return Result.Success(workspace.ToDto("admin"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace by ID for system admin. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SelectWorkspaceResponse>> SelectWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var activeMemberStatus = WorkspaceMemberStatus.Active.ToStorageValue();
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId
                     && m.UserId == userId
                     && m.RemovedAt == null
                     && m.Status.ToLower() == activeMemberStatus,
                "",
                ct);

            if (member == null)
            {
                return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);

            // `GetByIdAsync` is a raw `FindAsync`; it has no soft-delete or IsActive filter, so a
            // membership row alone is not proof the workspace is still usable. Membership survives
            // both deletion and deactivation, which means without these two checks a stale tab or a
            // hand-typed URL could pin a dead workspace into the user's Redis active context and
            // keep every later request scoped to it.
            if (workspace == null || workspace.DeletedAt != null)
            {
                return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // Deactivated is distinct from deleted for the person reading the message, but both are
            // 404 on the wire: the workspace is not selectable and the client treats them the same.
            if (!workspace.IsActive)
            {
                return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.WorkspaceInactive, ErrorCodes.NotFound);
            }

            var role = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var membershipType = member.MembershipType;

            await _workspaceCache.SetActiveWorkspaceDetailsAsync(userId, workspaceId, role, membershipType, ct);

            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var response = new SelectWorkspaceResponse(
                workspaceId,
                workspace.Name,
                workspace.Slug,
                role,
                membershipType,
                config.DefaultLanguage);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while selecting workspace. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.UnexpectedErrorSelectingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceSettingsDto>> GetWorkspaceSettingsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            if (member == null)
            {
                return Result.Failure<WorkspaceSettingsDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<WorkspaceSettingsDto>(WorkspaceConstants.Errors.OnlyOwnerAdminCanUpdateSettings, ErrorCodes.Forbidden);
            }

            var settings = await _unitOfWork.WorkspaceRepository.GetSettingsAsync(workspaceId, ct);

            // The settings JSON carries a VerifiedDomains list, but VerifiedDomainService writes
            // domains to workspace_verified_domains and never touches that JSON, so the stored
            // copy drifts the moment a domain is added or revoked. Overwrite it with the table on
            // the way out: the DTO stays a faithful view, and no caller can be misled into
            // treating the stale copy as policy.
            settings.VerifiedDomains = await WorkspaceHelper.GetActiveVerifiedDomainsAsync(_unitOfWork, workspaceId, ct);
            return Result.Success(settings.ToSettingsDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceSettingsDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateWorkspaceSettingsAsync(Guid workspaceId, WorkspaceSettingsDto settings, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (!execRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanUpdateSettings, ErrorCodes.Forbidden);
            }

            // Read once, from the table that owns them. Used both to validate the request and to
            // refresh the mirror written below, so the two can never disagree within this call.
            var activeVerifiedDomains = await WorkspaceHelper.GetActiveVerifiedDomainsAsync(_unitOfWork, workspaceId, ct);

            var settingsValidation = WorkspaceSettingsValidator.Validate(settings, activeVerifiedDomains);
            if (!settingsValidation.IsValid)
            {
                return Result.Failure(settingsValidation.ErrorMessage, ErrorCodes.ValidationError);
            }

            var currentConfig = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var ownerOnlyPolicyChanged = currentConfig.AllowExternalCollaboration != settings.AllowExternalCollaboration;
            if (ownerOnlyPolicyChanged && !execRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerCanModifyPolicySettings, ErrorCodes.Forbidden);
            }

            // The domain lifecycle belongs to VerifiedDomainService — it owns the Owner-only
            // check, the public-domain refusal, the cross-workspace uniqueness check, and the two
            // revoke guards. This endpoint used to carry its own partial copy of those rules,
            // driven by whatever VerifiedDomains the client happened to send. That copy could
            // only ever be a second, weaker opinion about the same table, so the incoming list is
            // now ignored outright and replaced with the table below.
            var newConfig = settings.ToConfiguration();
            newConfig.VerifiedDomains = activeVerifiedDomains.ToList();
            var updated = await _unitOfWork.WorkspaceRepository.UpdateSettingsAsync(workspaceId, newConfig, userId, ct);
            if (!updated)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-263: max_active_rooms is an entitlement, so the owner's chosen value has to reach
            // the resolver — it is the only code that knows the plan ceiling and therefore the only
            // code that can enforce "a workspace may tighten but never loosen". Billing rejects a
            // loosening value and the settings save is refused with billing's own reason.
            //
            // The JSON copy above is still written. It is what every existing workspace's number
            // lives in, and it remains the cold-start fallback until a snapshot arrives.
            if (_billingSubscriptionClient is not null && settings.MaxActiveRooms > 0)
            {
                var rejection = await _billingSubscriptionClient.ApplyWorkspaceEntitlementOverridesAsync(
                    workspaceId,
                    new Dictionary<string, string>
                    {
                        [EntitlementKeys.MaxActiveRooms] = settings.MaxActiveRooms.ToString(CultureInfo.InvariantCulture)
                    },
                    userId,
                    ct);

                if (rejection != null)
                {
                    return Result.Failure(rejection, ErrorCodes.ValidationError);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> SoftDeleteWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null || workspace.DeletedAt != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (!execRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerCanDeleteWorkspace, ErrorCodes.Forbidden);
            }

            workspace.DeletedAt = DateTime.UtcNow;
            workspace.UpdatedBy = userId;

            _unitOfWork.WorkspaceRepository.Update(workspace);
            await _eventPublisher.PublishWorkspaceDeletedAsync(workspaceId, userId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting workspace. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}

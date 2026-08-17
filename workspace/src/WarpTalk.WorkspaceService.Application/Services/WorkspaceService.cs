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

            // ── Caller eligibility ────────────────────────────────────────────────
            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, user.Email, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.ValidationError);
            }

            // WT-437 (Linear): one OWNED workspace per account, full stop.
            var ownerRoleName = WorkspaceMemberRole.Owner.ToRoleName();
            var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(ownerRoleName, ct);
            if (!ownerRoleId.HasValue)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.RequiredOwnerRoleNotFound, ErrorCodes.ValidationError);
            }

            var alreadyOwnsWorkspace = await _unitOfWork.WorkspaceMemberRepository.AnyAsync(
                m => m.UserId == userId
                     && m.RemovedAt == null
                     && m.RoleId == ownerRoleId.Value
                     && m.Workspace.DeletedAt == null,
                ct);
            if (alreadyOwnsWorkspace)
            {
                return Result.Failure<WorkspaceDto>(
                    WorkspaceConstants.Errors.UserAlreadyOwnsWorkspace, ErrorCodes.ValidationError);
            }

            // ── Which membership policy is being asked for ────────────────────────
            var requireVerified = request.RequireVerifiedDomainForInternal ?? true;

            var domainsToVerify = (request.VerifiedDomains ?? new List<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requireVerified && domainsToVerify.Count == 0 && !emailAddress.IsPublicDomain)
            {
                domainsToVerify.Add(emailAddress.Domain);
            }

            requireVerified = domainsToVerify.Count > 0;

            if (requireVerified && emailAddress.IsPublicDomain)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.PublicEmailDomainCannotCreateWorkspace, ErrorCodes.ValidationError);
            }

            // ── Domain claims ─────────────────────────────────────────────────────
            foreach (var domain in domainsToVerify)
            {
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

            workspace.RequireVerifiedDomainForInternal = requireVerified;
            workspace.AllowExternalCollaboration = config.AllowExternalCollaboration;

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
                config.DefaultLanguage,
                member.CanCreateMeetings);
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

            // Any active member may READ. The Owner/Admin gate that used to sit here was an
            // UPDATE-era rule applied to GET — its own error constant is named
            // OnlyOwnerAdminCanUpdateSettings — and the read's consumers are ordinary members:
            // the join page and the create-room dialog both ask for these settings to learn the
            // workspace's language policy and defaults, so every plain Member got a 403 the
            // moment either surface loaded ("sao nó báo ws/setting 403 v"). Nothing in this
            // document is a secret from the workspace's own members — it is the rules they are
            // being asked to follow. Writing stays Owner/Admin-only in UpdateWorkspaceSettings.
            var settings = await _unitOfWork.WorkspaceRepository.GetSettingsAsync(workspaceId, ct);
            settings.VerifiedDomains = await WorkspaceHelper.GetActiveVerifiedDomainsAsync(_unitOfWork, workspaceId, ct);

            // The ceiling travels WITH the setting, because the setting alone is not the rule.
            // Meeting creation enforces the tighter of the two (WorkspaceDirectoryService
            // .ResolveMaxActiveRooms), so a page that showed only the stored number was reporting
            // a limit the product does not apply — which is exactly the bug: settings said 20,
            // room creation refused at 5, and nothing on screen connected the two.
            var snapshot = await _unitOfWork.WorkspaceEntitlementSnapshotRepository
                .GetForWorkspaceAsync(workspaceId, ct);
            var entitlements = snapshot == null
                ? WorkspaceEntitlements.Unknown
                : WorkspaceEntitlements.FromSnapshot(snapshot.EntitlementsJson, snapshot.HasActiveSubscription);
            var ceiling = entitlements.SelfServiceLimit(EntitlementKeys.MaxActiveRooms);

            return Result.Success(settings.ToSettingsDto() with
            {
                MaxActiveRoomsCeiling = ceiling.HasValue
                    ? (int)Math.Clamp(ceiling.Value, int.MinValue, int.MaxValue)
                    : null,
                MaxActiveRoomsCeilingSource = ceiling.HasValue
                    ? entitlements.Source(EntitlementKeys.MaxActiveRooms)
                    : null,
            });
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

            // RequireVerifiedDomainForInternal is derived from the domain list, so this endpoint
            // cannot change it — not even for an Owner. Refusing a *different* value rather than
            // the field's presence keeps read-modify-write honest: clients send back the whole
            // settings document, and echoing what they were given must not be an error.
            //
            // The alternative, owner-gating it, would have left two ways to set one value: an
            // Owner could switch the policy off while verified domains remained, which is the
            // exact state WT-179 was about.
            if (settings.RequireVerifiedDomainForInternal != (activeVerifiedDomains.Count > 0))
            {
                return Result.Failure(WorkspaceConstants.Errors.RequireVerifiedDomainIsDerived, ErrorCodes.ValidationError);
            }

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

            if (settings.VerifiedDomains != null && settings.VerifiedDomains.Any())
            {
                foreach (var domain in settings.VerifiedDomains)
                {
                    if (EmailAddress.IsPublicDomainName(domain))
                    {
                        return Result.Failure(WorkspaceConstants.Errors.CannotVerifyPublicDomain, ErrorCodes.ValidationError);
                    }
                }
            }

            // Check if any domain is being removed via settings update
            var removedDomains = currentConfig.VerifiedDomains
                .Except(settings.VerifiedDomains ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (removedDomains.Any())
            {
                var newDomainsSet = (settings.VerifiedDomains ?? new List<string>())
                    .Select(d => d.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var activeInternalMembers = await _unitOfWork.WorkspaceMemberRepository.FindAsync(
                    m => m.WorkspaceId == workspaceId && m.RemovedAt == null && m.MembershipType == MembershipType.Internal.ToString(),
                    "",
                    ct);

                if (activeInternalMembers.Any())
                {
                    var activeInternalMemberUsers = await Task.WhenAll(
                        activeInternalMembers.Select(m => _authIdentity.GetUserByIdAsync(m.UserId, ct)));

                    var activeInternalMemberDomains = activeInternalMemberUsers
                        .Where(user => !string.IsNullOrWhiteSpace(user?.Email))
                        .Select(user => user!.Email.Split('@').LastOrDefault()?.Trim().ToLowerInvariant())
                        .Where(domain => !string.IsNullOrWhiteSpace(domain))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var removedDomain in removedDomains)
                    {
                        var targetDomain = removedDomain.Trim().ToLowerInvariant();
                        if (activeInternalMemberDomains.Contains(targetDomain) && !newDomainsSet.Contains(targetDomain))
                            return Result.Failure(WorkspaceConstants.Errors.CannotRevokeDomainWithActiveMembers, ErrorCodes.ValidationError);
                    }
                }
            }

            // WT-263: max_active_rooms is an entitlement, so the owner's chosen value has to reach
            // the resolver — it is the only code that knows the plan ceiling and therefore the only
            // code that can enforce "a workspace may tighten but never loosen". Billing rejects a
            // loosening value and the settings save is refused with billing's own reason.
            //
            // WT-430: THIS RUNS BEFORE THE WRITE, and the order is the fix. It used to sit after
            // UpdateSettingsAsync and SaveChangesAsync, so a value billing refused had already been
            // committed by the time the refusal was returned: production carried a stored
            // MaxActiveRooms of 20 against a ceiling of 5, and the enforcement error had to quote
            // both — "the workspace setting of 20 cannot raise it". The caller saw a failure while
            // the database kept the number.
            //
            // A billing outage still returns null (accepted) by design — see the client. That is a
            // tightening-only write, so an unreachable billing service cannot grant anything.
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

            // The JSON copy is still written. It is what every existing workspace's number lives in,
            // and it remains the cold-start fallback until a snapshot arrives.
            var newConfig = settings.ToConfiguration();
            var updated = await _unitOfWork.WorkspaceRepository.UpdateSettingsAsync(workspaceId, newConfig, userId, ct);
            if (!updated)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            await _unitOfWork.SaveChangesAsync(ct);

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

            var heldDomains = await _unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
                vd => vd.WorkspaceId == workspaceId && vd.RevokedAt == null,
                "",
                ct);

            foreach (var heldDomain in heldDomains)
            {
                heldDomain.SoftRevoke(userId);
                _unitOfWork.WorkspaceVerifiedDomainRepository.Update(heldDomain);
            }

            var deletedAt = DateTime.UtcNow;
            workspace.DeletedAt = deletedAt;
            workspace.UpdatedBy = userId;
            workspace.RequireVerifiedDomainForInternal = false;

            // WT-417: the members go with it.
            //
            // This used to stamp the workspace and leave every membership row untouched, with
            // RemovedAt still NULL — so deleting a workspace left behind rows that every
            // membership lookup in the service correctly reads as LIVE memberships of a workspace
            // that no longer exists. They are unreachable (the workspace is filtered out of every
            // listing by DeletedAt) and permanent (nothing un-deletes a workspace — ReactivateAsync
            // flips IsActive, not this), and because UNIQUE (workspace_id, user_id) has no
            // `WHERE removed_at IS NULL`, they hold their slot against any future rejoin.
            //
            // That is the orphan the ticket is named for. Fixing the acceptance guard alone would
            // have left this generating fresh orphans on every delete.
            var members = await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct);
            foreach (var member in members)
            {
                // WT-434 (Linear): GetActiveMembersByWorkspaceAsync is AsNoTracking, but
                // `executingMember` above came through the tracked FirstOrDefaultAsync — and the
                // OWNER is always in both. Update() on the detached copy of a row the context
                // already tracks throws the EF identity-map InvalidOperationException, the catch
                // below converts it to UnexpectedError, and the controller (which had no 500
                // branch) surfaced it as 400 Bad Request. Deleting a workspace therefore failed
                // 100% of the time, for exactly the one role allowed to try.
                // Matched on UserId, not Id: (workspace_id, user_id) is UNIQUE so it identifies
                // the row, and unlike Id it can never be an unset Guid.Empty on both sides.
                var target = member.UserId == executingMember.UserId ? executingMember : member;
                target.RemovedAt = deletedAt;
                target.RemovedBy = userId;
                _unitOfWork.WorkspaceMemberRepository.Update(target);
            }

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers.VerifiedDomain;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Services;

public class VerifiedDomainService : IVerifiedDomainService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ILogger<VerifiedDomainService> _logger;

    public VerifiedDomainService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        ILogger<VerifiedDomainService> logger)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ADD
    // ─────────────────────────────────────────────────────────────────────────

    public Task<Result<VerifiedDomainDto>> AddDomainAsync(
        Guid workspaceId, string domain, Guid userId, CancellationToken ct = default)
        => AddDomainAsync(workspaceId, domain, userId, consentVersion: null, ct);

    public async Task<Result<VerifiedDomainDto>> AddDomainAsync(
        Guid workspaceId, string domain, Guid userId, string? consentVersion, CancellationToken ct = default)
    {
        try
        {
            // 1. Workspace must exist
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);

            // 2. Caller must be an active Owner
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwner())
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, ErrorCodes.Forbidden);

            // 3. Normalise and validate domain
            domain = domain.Trim().ToLowerInvariant();

            if (EmailAddress.IsPublicDomainName(domain))
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.CannotVerifyPublicDomain, ErrorCodes.ValidationError);

            // 3a. Which trust tier this claim rests on.
            //
            // The rule used to be "the caller may only claim the domain of their own account
            // email" — full stop, refusing everything else. That was the only thing standing
            // between "non-public = enterprise-owned" (the actual business rule: every domain
            // added here is already treated as if it passed DNS verification, and WarpTalk is
            // not on the hook for a wrong claim) and a company with several domains being unable
            // to register any but the first from one Owner account.
            //
            // Multi-domain is now allowed outright — the schema always supported it (see the
            // partial unique index on `domain WHERE status = 'verified'`, which caps a domain at
            // one workspace, not a workspace at one domain). What changes with the domain is how
            // much evidence backs the claim:
            //   - matches the caller's own email  → owner_email, self-evidencing
            //   - anything else                   → self_asserted, and the Owner must say so
            var caller = await _authIdentity.GetUserByIdAsync(userId, ct);
            if (caller == null || !EmailAddress.TryParse(caller.Email, out var callerEmail) || callerEmail == null)
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.InvalidUserEmail, ErrorCodes.ValidationError);

            var isOwnerEmailDomain = string.Equals(domain, callerEmail.Domain, StringComparison.OrdinalIgnoreCase);
            var verificationMethod = isOwnerEmailDomain
                ? VerifiedDomainVerificationMethods.OwnerEmail
                : VerifiedDomainVerificationMethods.SelfAsserted;

            if (!isOwnerEmailDomain && string.IsNullOrWhiteSpace(consentVersion))
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.ConsentRequiredForSelfAssertedDomain, ErrorCodes.ValidationError);

            // 4. Domain must not already be claimed by another workspace
            var owningWorkspaceId = await WorkspaceHelper.GetWorkspaceIdVerifyingDomainAsync(_unitOfWork, domain, ct);
            if (owningWorkspaceId.HasValue && owningWorkspaceId.Value != workspaceId)
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.DomainRegisteredElsewhere, ErrorCodes.ValidationError);

            // 5. Domain must not already be active in this workspace
            var duplicate = await _unitOfWork.WorkspaceVerifiedDomainRepository.AnyAsync(
                vd => vd.WorkspaceId == workspaceId
                      && vd.Domain == domain
                      && vd.RevokedAt == null,
                ct);
            if (duplicate)
                return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.DomainAlreadyAddedToWorkspace, ErrorCodes.ValidationError);

            // 6. Trust the domain immediately — business rule: non-public = enterprise-owned.
            // The consent version (for self_asserted) is written onto the row itself, in this
            // same INSERT, rather than to a separate audit call — the evidence for a claim
            // cannot be allowed to succeed or fail independently of the claim.
            var entry = VerifiedDomainMapper.ToEntity(
                workspaceId,
                domain,
                userId,
                verificationMethod,
                consentEvidence: isOwnerEmailDomain ? verificationMethod : consentVersion!);

            await _unitOfWork.WorkspaceVerifiedDomainRepository.AddAsync(entry, ct);

            // PERSISTED BEFORE THE RECOMPUTE, AND THAT ORDER IS THE WHOLE FIX.
            //
            // RecomputeDomainPolicyAsync COUNTS the domain list through
            // GetActiveVerifiedDomainsAsync, which is a database query (GenericRepository.FindAsync
            // is `_dbSet.Where(predicate).ToListAsync()`). A row that has only been AddAsync'd is
            // pending in the change tracker and does not exist in the database yet, so that query
            // cannot return it.
            //
            // Recomputing first therefore counted the workspace's domains WITHOUT the one being
            // added. For a workspace with no other domain that is a count of zero, so the policy
            // stayed off — and "adding the first domain is what makes this a domain-verified
            // workspace", the sentence this comment replaced, was never true through this path.
            //
            // The consequence is not a stale flag. UpdateWorkspaceSettings refuses any request
            // whose RequireVerifiedDomainForInternal disagrees with the real domain count, and the
            // settings PATCH merges the stored value into the document it validates. So a workspace
            // left in this state cannot save ANY setting — language, retention, max rooms, all of
            // it — and the error names verified domains, which is not what the Owner was editing.
            //
            // Two saves rather than one because this unit of work exposes no transaction. The
            // second writes a single boolean; if it fails the state is what today's bug already
            // produces, rather than something new.
            await _unitOfWork.SaveChangesAsync(ct);

            await WorkspaceHelper.RecomputeDomainPolicyAsync(_unitOfWork, workspace, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(entry.ToDto());
        }
        catch (Exception ex) when (_unitOfWork.WorkspaceVerifiedDomainRepository.IsDomainAlreadyClaimedViolation(ex))
        {
            // Two requests can both pass the "not already claimed" check above and then race to
            // insert — the partial unique index on (domain) WHERE status = 'verified' is what
            // actually decides who wins. The loser used to surface as an unhandled 500: the index
            // was protecting the data correctly all along, only the reported error was wrong.
            _logger.LogInformation(
                "Concurrent verified-domain claim lost the race. WorkspaceId: {WorkspaceId}, Domain: {Domain}",
                workspaceId, domain);
            return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.DomainRegisteredElsewhere, ErrorCodes.ValidationError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding verified domain. WorkspaceId: {WorkspaceId}, Domain: {Domain}, UserId: {UserId}",
                workspaceId, domain, userId);
            return Result.Failure<VerifiedDomainDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIST
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<List<VerifiedDomainDto>>> ListDomainsAsync(
        Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            // Caller must be an active member (Owner or Admin)
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
                return Result.Failure<List<VerifiedDomainDto>>(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
                return Result.Failure<List<VerifiedDomainDto>>(WorkspaceConstants.Errors.OnlyOwnerAdminCanUpdateSettings, ErrorCodes.Forbidden);

            var domains = await _unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
                vd => vd.WorkspaceId == workspaceId && vd.RevokedAt == null,
                "",
                ct);

            List<VerifiedDomainDto> dtos = domains.Select(d => d.ToDto()).ToList();
            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing verified domains. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<List<VerifiedDomainDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REVOKE
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<Result> RevokeDomainAsync(
        Guid workspaceId, Guid domainId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            // 1. Workspace must exist
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);

            // 2. Caller must be an active Owner
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwner())
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, ErrorCodes.Forbidden);

            // 3. Domain entry must exist and be active
            var entry = await _unitOfWork.WorkspaceVerifiedDomainRepository.FirstOrDefaultAsync(
                vd => vd.Id == domainId && vd.WorkspaceId == workspaceId && vd.RevokedAt == null,
                "",
                ct);
            if (entry == null)
                return Result.Failure(WorkspaceConstants.Errors.VerifiedDomainNotFound, ErrorCodes.NotFound);

            // The "cannot revoke the last domain while domain verification is required" guard used
            // to sit here. It became a contradiction: the policy is now derived from the domain
            // list, so revoking the last domain IS how a workspace stops requiring one. Keeping
            // the guard left no way back to manually-assigned membership — the workspace would be
            // stuck as domain-verified for good. Losing that policy is a real change, so it is
            // confirmed in the UI and recorded, rather than blocked here.
            //
            // The guard below is a different rule and stays: it protects members who are already
            // Internal by virtue of this domain.

            // 5. Guard: cannot revoke domain if active internal members rely on this domain
            var activeInternalMembers = await _unitOfWork.WorkspaceMemberRepository.FindAsync(
                m => m.WorkspaceId == workspaceId && m.RemovedAt == null && m.MembershipType == MembershipType.Internal.ToString(),
                "",
                ct);

            if (activeInternalMembers.Any())
            {
                var targetDomain = entry.Domain.Trim().ToLowerInvariant();
                var remainingActiveDomains = (await _unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
                    vd => vd.WorkspaceId == workspaceId && vd.Id != domainId && vd.RevokedAt == null,
                    "",
                    ct)).Select(d => d.Domain.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var activeMember in activeInternalMembers)
                {
                    var user = await _authIdentity.GetUserByIdAsync(activeMember.UserId, ct);
                    if (user != null && !string.IsNullOrWhiteSpace(user.Email))
                    {
                        var memberEmailDomain = user.Email.Split('@').LastOrDefault()?.Trim().ToLowerInvariant();
                        if (string.Equals(memberEmailDomain, targetDomain, StringComparison.OrdinalIgnoreCase))
                        {
                            if (memberEmailDomain != null && !remainingActiveDomains.Contains(memberEmailDomain))
                            {
                                return Result.Failure(WorkspaceConstants.Errors.CannotRevokeDomainWithActiveMembers, ErrorCodes.ValidationError);
                            }
                        }
                    }
                }
            }

            // 6. Soft-revoke
            entry.SoftRevoke(userId);

            _unitOfWork.WorkspaceVerifiedDomainRepository.Update(entry);

            // Same ordering as the add path, for the mirror-image reason. The revocation is a
            // pending UPDATE, so the row still satisfies `RevokedAt == null` in the database and
            // the count would include a domain that is on its way out — leaving the policy ON
            // after the last domain was revoked. Committed truth first, then recompute.
            await _unitOfWork.SaveChangesAsync(ct);

            await WorkspaceHelper.RecomputeDomainPolicyAsync(_unitOfWork, workspace, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking verified domain. WorkspaceId: {WorkspaceId}, DomainId: {DomainId}, UserId: {UserId}",
                workspaceId, domainId, userId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}

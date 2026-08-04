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

    public async Task<Result<VerifiedDomainDto>> AddDomainAsync(
        Guid workspaceId, string domain, Guid userId, CancellationToken ct = default)
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

            // 6. Trust the domain immediately — business rule: non-public = enterprise-owned
            var entry = VerifiedDomainMapper.ToEntity(workspaceId, domain, userId);

            await _unitOfWork.WorkspaceVerifiedDomainRepository.AddAsync(entry, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(entry.ToDto());
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

            // 4. Guard: cannot revoke the last domain when RequireVerifiedDomainForInternal is enabled
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            if (config.RequireVerifiedDomainForInternal)
            {
                var activeCount = (await _unitOfWork.WorkspaceVerifiedDomainRepository.FindAsync(
                    vd => vd.WorkspaceId == workspaceId && vd.RevokedAt == null,
                    "",
                    ct)).Count;

                if (activeCount <= 1)
                    return Result.Failure(WorkspaceConstants.Errors.CannotRevokeLastDomain, ErrorCodes.ValidationError);
            }

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

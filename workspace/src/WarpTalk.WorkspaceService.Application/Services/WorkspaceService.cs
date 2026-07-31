using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    public WorkspaceService(
        IUnitOfWork unitOfWork, 
        IWorkspaceCacheService workspaceCache, 
        ILogger<WorkspaceService> logger,
        IAuthIdentityClient authIdentity,
        IWorkspaceEventPublisher eventPublisher)
    {
        _unitOfWork = unitOfWork;
        _workspaceCache = workspaceCache;
        _logger = logger;
        _authIdentity = authIdentity;
        _eventPublisher = eventPublisher;
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

            var domainsToVerify = new List<string>();
            bool requireVerified;

            if (request.VerifiedDomains != null && request.VerifiedDomains.Any())
            {
                domainsToVerify = request.VerifiedDomains;
                requireVerified = request.RequireVerifiedDomainForInternal ?? true;
            }
            else
            {
                if (request.RequireVerifiedDomainForInternal == true)
                {
                    domainsToVerify = new List<string> { emailAddress.Domain };
                    requireVerified = true;
                }
                else
                {
                    requireVerified = false;
                }
            }

            if (requireVerified)
            {
                var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, user.Email, ct);
                if (isInternalElsewhere)
                {
                    return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.ValidationError);
                }

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
                    await _unitOfWork.Repository<WorkspaceVerifiedDomain>().AddAsync(verifiedDomain, ct);
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
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null,
                "",
                ct
            );

            if (member == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
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

    public async Task<Result<SelectWorkspaceResponse>> SelectWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            if (member == null)
            {
                return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<SelectWorkspaceResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var user = await _authIdentity.GetUserByIdAsync(userId, ct);
            var role = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            var membershipTypeEnum = await WorkspaceHelper.DetermineMembershipTypeAsync(_unitOfWork, user?.Email, workspace, ct);
            var membershipType = membershipTypeEnum.ToString();

            await _workspaceCache.SetActiveWorkspaceDetailsAsync(userId, workspaceId, role, membershipType, ct);

            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var response = new SelectWorkspaceResponse(workspaceId, workspace.Name, workspace.Slug, config.DefaultLanguage);
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

            var settingsValidation = WorkspaceSettingsValidator.Validate(settings);
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

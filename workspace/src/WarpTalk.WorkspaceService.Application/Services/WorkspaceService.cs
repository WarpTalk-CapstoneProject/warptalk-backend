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
using WarpTalk.WorkspaceService.Application.Mappers.Workspace;
using WarpTalk.WorkspaceService.Application.Mappers.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly IAuthIdentityClient _authIdentity;

    public WorkspaceService(
        IUnitOfWork unitOfWork, 
        IWorkspaceCacheService workspaceCache, 
        ILogger<WorkspaceService> logger,
        IAuthIdentityClient authIdentity)
    {
        _unitOfWork = unitOfWork;
        _workspaceCache = workspaceCache;
        _logger = logger;
        _authIdentity = authIdentity;
    }

    private async Task<string> GetRoleNameByIdAsync(Guid roleId, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByIdAsync(roleId, ct);
        return role?.Name ?? "Member";
    }

    private async Task<Guid?> GetRoleIdByNameAsync(string roleName, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByNameAsync(roleName, ct);
        return role?.Id;
    }

    public async Task<Result<WorkspaceDto>> CreateWorkspaceAsync(CreateWorkspaceRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Result.Failure<WorkspaceDto>("Workspace name is required.", ErrorCodes.ValidationError);
            }

            var user = await _authIdentity.GetUserByIdAsync(userId, ct);
            if (user == null)
            {
                return Result.Failure<WorkspaceDto>("User not found.", ErrorCodes.UserNotFound);
            }

            if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<WorkspaceDto>("Invalid user email.", ErrorCodes.ValidationError);
            }

            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, user.Email, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure<WorkspaceDto>("User is already an internal member of another Enterprise Workspace.", ErrorCodes.ValidationError);
            }

            var owningWorkspaceId = await WorkspaceHelper.GetWorkspaceIdVerifyingDomainAsync(_unitOfWork, emailAddress.Domain, ct);
            if (owningWorkspaceId.HasValue)
            {
                return Result.Failure<WorkspaceDto>("This email belongs to a corporate domain registered with another workspace.", ErrorCodes.ValidationError);
            }

            var baseSlug = SlugHelper.GenerateSlug(request.Name);
            var slug = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _unitOfWork.WorkspaceRepository, ct);

            var workspace = request.ToEntity(slug, userId);
            var config = new WorkspaceConfiguration
            {
                VerifiedDomains = new List<string> { emailAddress.Domain }
            };
            workspace.Settings = JsonSerializer.Serialize(config);

            var ownerRoleName = WorkspaceMemberRole.Owner.ToRoleName();
            var ownerRoleId = await GetRoleIdByNameAsync(ownerRoleName, ct);
            if (!ownerRoleId.HasValue)
            {
                return Result.Failure<WorkspaceDto>("Required owner role not found.", ErrorCodes.ValidationError);
            }
            var workspaceMember = WorkspaceMemberMapper.CreateOwnerMember(workspace.Id, userId, ownerRoleId.Value);

            await _unitOfWork.WorkspaceRepository.AddAsync(workspace, ct);
            await _unitOfWork.WorkspaceMemberRepository.AddAsync(workspaceMember, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(workspace.ToDto(WorkspaceMemberRole.Owner));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating workspace. UserId: {UserId}, WorkspaceName: {WorkspaceName}", userId, request.Name);
            return Result.Failure<WorkspaceDto>("An unexpected error occurred while creating the workspace.", ErrorCodes.InternalServerError);
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
                var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                    m => m.WorkspaceId == ws.Id && m.UserId == userId, 
                    "", 
                    ct
                );
                var defaultRoleName = WorkspaceMemberRole.Member.ToRoleName();
                var roleName = defaultRoleName;
                if (member != null)
                {
                    roleName = await GetRoleNameByIdAsync(member.RoleId, ct);
                }

                workspaceDtos.Add(ws.ToDto(roleName));
            }

            var pagedResult = new PagedResult<WorkspaceDto>(workspaceDtos, query.Page, query.PageSize, totalCount);
            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspaces for user. UserId: {UserId}", userId);
            return Result.Failure<PagedResult<WorkspaceDto>>("An unexpected error occurred while fetching workspaces.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDto>> GetWorkspaceByIdAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId,
                "",
                ct
            );

            if (member == null)
            {
                return Result.Failure<WorkspaceDto>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceDto>("Workspace not found.", ErrorCodes.NotFound);
            }

            var roleName = await GetRoleNameByIdAsync(member.RoleId, ct);
            return Result.Success(workspace.ToDto(roleName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace by ID. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceDto>("An unexpected error occurred while fetching the workspace.", ErrorCodes.InternalServerError);
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
                return Result.Failure<SelectWorkspaceResponse>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<SelectWorkspaceResponse>("Workspace not found.", ErrorCodes.NotFound);
            }

            var user = await _authIdentity.GetUserByIdAsync(userId, ct);
            var role = await GetRoleNameByIdAsync(member.RoleId, ct);
            var membershipType = WorkspaceHelper.DetermineMembershipType(user?.Email, workspace).ToString();

            await _workspaceCache.SetActiveWorkspaceDetailsAsync(userId, workspaceId, role, membershipType, ct);

            var response = new SelectWorkspaceResponse(workspaceId, workspace.Name, workspace.Slug);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while selecting workspace. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<SelectWorkspaceResponse>("An unexpected error occurred while selecting the workspace.", ErrorCodes.InternalServerError);
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
                return Result.Failure<WorkspaceSettingsDto>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var settings = await _unitOfWork.WorkspaceRepository.GetSettingsAsync(workspaceId, ct);
            return Result.Success(settings.ToSettingsDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceSettingsDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateWorkspaceSettingsAsync(Guid workspaceId, WorkspaceSettingsDto settings, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = await GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (execRoleName != WorkspaceMemberRole.Owner.ToRoleName() && execRoleName != WorkspaceMemberRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can update workspace settings.", ErrorCodes.Forbidden);
            }

            if (settings == null)
            {
                return Result.Failure("Invalid settings payload.", ErrorCodes.ValidationError);
            }

            var currentConfig = WorkspaceHelper.GetWorkspaceConfig(workspace);
            if (execRoleName == WorkspaceMemberRole.Admin.ToRoleName())
            {
                if (currentConfig.AllowExternalCollaboration != settings.AllowExternalCollaboration)
                {
                    return Result.Failure("Only the workspace owner can modify AllowExternalCollaboration setting.", ErrorCodes.Forbidden);
                }
            }

            var newConfig = settings.ToConfiguration();
            var updated = await _unitOfWork.WorkspaceRepository.UpdateSettingsAsync(workspaceId, newConfig, userId, ct);
            if (!updated)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}

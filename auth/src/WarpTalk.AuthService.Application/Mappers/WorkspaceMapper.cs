using System;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Validators;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Extensions;

namespace WarpTalk.AuthService.Application.Mappers;

public static class WorkspaceMapper
{
    public static WorkspaceDto ToDto(this Workspace workspace, string role)
    {
        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Slug,
            null, // Description is not present in raw scaffolded DB schema
            workspace.LogoUrl,
            role,
            workspace.Type,
            workspace.CreatedAt
        );
    }

    public static WorkspaceDto ToDto(this Workspace workspace, WorkspaceUserRole role)
    {
        return ToDto(workspace, role.ToRoleName());
    }

    public static Workspace ToEntity(this CreateWorkspaceRequest request, string slug, Guid ownerId)
    {
        return new Workspace
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = slug,
            LogoUrl = request.LogoUrl,
            OwnerId = ownerId,
            PlanTier = AuthConstants.PlanTierFree,
            Settings = "{}",
            Type = WorkspaceType.Business.ToString().ToLower(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static Workspace CreatePersonalWorkspace(string fullName, string slug, Guid ownerId)
    {
        return new Workspace
        {
            Id = Guid.NewGuid(),
            Name = $"{fullName}'s Workspace",
            Slug = slug,
            LogoUrl = null,
            OwnerId = ownerId,
            PlanTier = AuthConstants.PlanTierFree,
            Settings = "{}",
            Type = WorkspaceType.Personal.ToString().ToLower(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceMember CreateOwnerMember(Guid workspaceId, Guid userId, Role? ownerRole)
    {
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = ownerRole?.Id ?? Guid.Empty,
            Role = ownerRole ?? new Role { Name = WorkspaceUserRole.Owner.ToRoleName() },
            Status = "Active",
            JoinedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceInvitationDto ToDto(this WorkspaceInvitation invitation)
    {
        WorkspaceInvitationValidator.ValidateForMapping(invitation);

        return new WorkspaceInvitationDto(
            invitation.Id,
            invitation.WorkspaceId,
            invitation.Email,
            invitation.Role.Name,
            invitation.Status,
            invitation.ExpiresAt,
            invitation.CreatedAt,
            invitation.AcceptedAt
        );
    }
}

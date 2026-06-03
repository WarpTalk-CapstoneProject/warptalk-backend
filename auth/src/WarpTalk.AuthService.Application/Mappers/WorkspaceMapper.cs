using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Validators;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Extensions;
using WarpTalk.AuthService.Domain.Settings;

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
            PlanTier = WorkspaceConstants.PlanTierFree,
            Settings = "{}",
            Type = WorkspaceType.Enterprise.ToString().ToLower(),
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
            Status = WorkspaceMemberStatus.Active,
            JoinedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceMember CreateInvitationMember(Guid workspaceId, Guid userId, Guid roleId)
    {
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active,
            JoinedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceInvitation CreateInvitation(Guid workspaceId, InviteMemberRequest request, Role role, Guid inviterUserId, string tokenHash)
    {
        return new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = request.Email,
            RoleId = role.Id,
            Role = role,
            InvitedBy = inviterUserId,
            TokenHash = tokenHash,
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
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
            invitation.Status.ToString(),
            invitation.ExpiresAt,
            invitation.CreatedAt,
            invitation.AcceptedAt
        );
    }

    public static WorkspaceMemberDto ToDto(this WorkspaceMember member, Workspace? workspace)
    {
        return new WorkspaceMemberDto(
            member.Id,
            member.WorkspaceId,
            member.UserId,
            member.User?.FullName ?? "Unknown",
            member.User?.Email ?? "Unknown",
            member.User?.AvatarUrl,
            member.Role?.Name ?? "Member",
            member.Status,
            member.JoinedAt,
            WorkspaceHelper.DetermineMembershipType(member, workspace).ToString()
        );
    }

    public static WorkspaceSettingsDto ToSettingsDto(this WorkspaceConfiguration config)
    {
        return new WorkspaceSettingsDto(
            config.DefaultLanguage,
            config.Timezone,
            config.AllowedTargetLanguages,
            config.VoiceCloningEnabled,
            config.MaxActiveRooms,
            config.ArtifactRetentionDays,
            config.EnforceHostApprovalDefault,
            config.VerifiedDomains,
            config.AllowExternalCollaboration
        );
    }

    public static WorkspaceConfiguration ToConfiguration(this WorkspaceSettingsDto dto)
    {
        return new WorkspaceConfiguration
        {
            DefaultLanguage = dto.DefaultLanguage,
            Timezone = dto.Timezone,
            AllowedTargetLanguages = dto.AllowedTargetLanguages,
            VoiceCloningEnabled = dto.VoiceCloningEnabled,
            MaxActiveRooms = dto.MaxActiveRooms,
            ArtifactRetentionDays = dto.ArtifactRetentionDays,
            EnforceHostApprovalDefault = dto.EnforceHostApprovalDefault,
            VerifiedDomains = dto.VerifiedDomains,
            AllowExternalCollaboration = dto.AllowExternalCollaboration
        };
    }
}


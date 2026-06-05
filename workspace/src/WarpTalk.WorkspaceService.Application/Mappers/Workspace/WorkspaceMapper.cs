using System;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Application.Mappers.Workspace;

public static class WorkspaceMapper
{
    public static WorkspaceDto ToDto(this Domain.Entities.Workspace workspace, string role)
    {
        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Slug,
            null, // Description is not present in raw scaffolded DB schema
            workspace.LogoUrl,
            role,
            workspace.CreatedAt
        );
    }

    public static WorkspaceDto ToDto(this Domain.Entities.Workspace workspace, WorkspaceMemberRole role)
    {
        return ToDto(workspace, role.ToRoleName());
    }

    public static Domain.Entities.Workspace ToEntity(this CreateWorkspaceRequest request, string slug, Guid ownerId)
    {
        return new Domain.Entities.Workspace
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = slug,
            LogoUrl = request.LogoUrl,
            OwnerId = ownerId,
            Settings = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
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

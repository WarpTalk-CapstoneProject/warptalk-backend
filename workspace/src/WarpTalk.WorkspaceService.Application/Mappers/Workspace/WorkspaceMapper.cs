using System;
using System.Collections.Generic;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceMapper
{
    public static WorkspaceDto ToDto(this Workspace workspace, string role)
    {
        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Slug,
            workspace.LogoUrl,
            role,
            workspace.CreatedAt
        );
    }

    public static WorkspaceDto ToDto(this Workspace workspace, WorkspaceMemberRole role)
    {
        return ToDto(workspace, role.ToRoleName());
    }

    public static Workspace ToEntity(this CreateWorkspaceRequest request, string slug, Guid ownerId, string settingsJson = "{}", DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new Workspace
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = slug,
            LogoUrl = request.LogoUrl,
            OwnerId = ownerId,
            Settings = settingsJson,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
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

    public static WorkspaceVerifiedDomain ToVerifiedDomainEntity(Guid workspaceId, string domain, Guid userId, string status = "verified", string verificationMethod = "system", DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceVerifiedDomain
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Domain = domain,
            Status = status,
            VerificationMethod = verificationMethod,
            VerificationToken = Guid.NewGuid().ToString(),
            VerifiedAt = now,
            VerifiedBy = userId,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
    }
}

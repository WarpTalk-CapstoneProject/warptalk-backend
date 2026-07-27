using System;
using System.Collections.Generic;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Settings;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceMapper
{
    public static WorkspaceDto ToDto(this Workspace workspace, string role, string membershipType = "Internal")
    {
        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Slug,
            workspace.LogoUrl,
            role,
            workspace.CreatedAt,
            membershipType,
            config.DefaultLanguage,
            role.IsOwnerOrAdmin()
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
            config.AllowExternalCollaboration,
            config.RequireVerifiedDomainForInternal,
            config.AiUsagePolicy?.ToDto(),
            config.IsProfanityFilterEnabled
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
            AllowExternalCollaboration = dto.AllowExternalCollaboration,
            RequireVerifiedDomainForInternal = dto.RequireVerifiedDomainForInternal,
            AiUsagePolicy = dto.AiUsagePolicy?.ToConfiguration(),
            IsProfanityFilterEnabled = dto.IsProfanityFilterEnabled
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

    public static AiUsagePolicyDto ToDto(this AiUsagePolicyConfiguration config)
    {
        return new AiUsagePolicyDto(
            true,
            config.RedactPii.ToDto(),
            config.Dlp.ToDto(),
            config.TranslationProfile.ToDto(),
            config.UseGlobalGlossary
        );
    }

    public static AiUsagePolicyConfiguration ToConfiguration(this AiUsagePolicyDto dto)
    {
        return new AiUsagePolicyConfiguration(
            true,
            dto.RedactPii.ToConfiguration(),
            dto.Dlp.ToConfiguration(),
            dto.TranslationProfile.ToConfiguration(),
            dto.UseGlobalGlossary
        );
    }

    public static PiiRedactionDto? ToDto(this PiiRedactionConfiguration? config)
    {
        if (config == null) return null;
        return new PiiRedactionDto(config.Enabled);
    }

    public static PiiRedactionConfiguration? ToConfiguration(this PiiRedactionDto? dto)
    {
        if (dto == null) return null;
        return new PiiRedactionConfiguration(dto.Enabled);
    }

    public static DlpDto? ToDto(this DlpConfiguration? config)
    {
        if (config == null) return null;
        return new DlpDto(config.Enabled, config.KeywordsBlacklist);
    }

    public static DlpConfiguration? ToConfiguration(this DlpDto? dto)
    {
        if (dto == null) return null;
        return new DlpConfiguration(dto.Enabled, dto.KeywordsBlacklist);
    }

    public static TranslationProfileDto? ToDto(this TranslationProfileConfiguration? config)
    {
        if (config == null) return null;
        return new TranslationProfileDto(
            config.TranslationTone,
            config.LanguageSpecificRules.ToDto()
        );
    }

    public static TranslationProfileConfiguration? ToConfiguration(this TranslationProfileDto? dto)
    {
        if (dto == null) return null;
        return new TranslationProfileConfiguration(
            dto.TranslationTone,
            dto.LanguageSpecificRules.ToConfiguration()
        );
    }

    public static LanguageSpecificRulesDto? ToDto(this LanguageSpecificRules? config)
    {
        if (config == null) return null;
        return new LanguageSpecificRulesDto(
            config.VietnameseHonorificStyle,
            config.JapaneseHonorificStyle
        );
    }

    public static LanguageSpecificRules? ToConfiguration(this LanguageSpecificRulesDto? dto)
    {
        if (dto == null) return null;
        return new LanguageSpecificRules(
            dto.VietnameseHonorificStyle,
            dto.JapaneseHonorificStyle
        );
    }
}

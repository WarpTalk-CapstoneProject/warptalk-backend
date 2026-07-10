using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Application.Mappers;

public static class VoiceProfileMapper
{
    public static VoiceProfile ToEntity(Guid userId, CreateVoiceProfileRequest request)
    {
        var now = DateTime.UtcNow;

        return new VoiceProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WorkspaceId = request.WorkspaceId,
            DisplayName = request.DisplayName?.Trim(),
            Provider = request.Provider?.Trim(),
            Status = VoiceProfileConstants.StatusPendingConsent,
            IsActive = true,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
    }

    public static VoiceSample ToSample(Guid voiceProfileId, Guid userId, AddVoiceSampleRequest request)
    {
        return new VoiceSample
        {
            Id = Guid.NewGuid(),
            VoiceProfileId = voiceProfileId,
            SampleType = request.SampleType.Trim().ToLowerInvariant(),
            FileUrl = request.FileUrl.Trim(),
            DurationSeconds = request.DurationSeconds,
            Language = request.Language,
            ContainsRawAudio = request.ContainsRawAudio,
            RetentionUntil = request.RetentionUntil,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static VoiceConsent ToGrantedConsent(Guid userId, Guid voiceProfileId, GrantVoiceConsentRequest request, string? ipAddress, string? userAgent)
    {
        var now = DateTime.UtcNow;

        return new VoiceConsent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            VoiceProfileId = voiceProfileId,
            ConsentType = request.ConsentType.Trim().ToLowerInvariant(),
            ConsentStatus = ConsentStatus.GRANTED,
            ConsentTextVersion = request.ConsentTextVersion.Trim(),
            GrantedAt = now,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = now
        };
    }

    public static VoiceConsent ToRevokedConsent(Guid userId, Guid voiceProfileId, RevokeVoiceConsentRequest request, string? ipAddress, string? userAgent)
    {
        var now = DateTime.UtcNow;

        return new VoiceConsent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            VoiceProfileId = voiceProfileId,
            ConsentType = request.ConsentType.Trim().ToLowerInvariant(),
            ConsentStatus = ConsentStatus.REVOKED,
            ConsentTextVersion = request.ConsentTextVersion.Trim(),
            RevokedAt = now,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = now
        };
    }

    public static VoiceProfileDto ToDto(VoiceProfile profile)
    {
        return new VoiceProfileDto(
            Id: profile.Id,
            UserId: profile.UserId,
            WorkspaceId: profile.WorkspaceId,
            DisplayName: profile.DisplayName,
            Provider: profile.Provider,
            EmbeddingRef: profile.EmbeddingRef,
            Status: profile.Status,
            IsActive: profile.IsActive,
            CreatedAt: profile.CreatedAt,
            UpdatedAt: profile.UpdatedAt,
            Samples: profile.Samples.Where(s => s.DeletedAt == null).OrderByDescending(s => s.CreatedAt).Select(ToDto).ToList(),
            Consents: profile.Consents.OrderByDescending(c => c.CreatedAt).Select(ToDto).ToList()
        );
    }

    public static VoiceSampleDto ToDto(VoiceSample sample)
    {
        return new VoiceSampleDto(
            Id: sample.Id,
            VoiceProfileId: sample.VoiceProfileId,
            SampleType: sample.SampleType,
            FileUrl: sample.FileUrl,
            DurationSeconds: sample.DurationSeconds,
            Language: sample.Language,
            ContainsRawAudio: sample.ContainsRawAudio,
            RetentionUntil: sample.RetentionUntil,
            CreatedAt: sample.CreatedAt
        );
    }

    public static VoiceConsentDto ToDto(VoiceConsent consent)
    {
        return new VoiceConsentDto(
            Id: consent.Id,
            UserId: consent.UserId,
            VoiceProfileId: consent.VoiceProfileId,
            ConsentType: consent.ConsentType,
            ConsentStatus: consent.ConsentStatus.ToString(),
            ConsentTextVersion: consent.ConsentTextVersion,
            GrantedAt: consent.GrantedAt,
            RevokedAt: consent.RevokedAt,
            CreatedAt: consent.CreatedAt
        );
    }
}

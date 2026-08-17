using System.Linq;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class VoiceProfileMapper
{
    public static VoiceProfileDto ToDto(VoiceProfile profile)
    {
        var activeConsent = profile.VoiceConsents
            .Where(consent =>
                string.Equals(consent.ConsentType, "VOICE_PROFILE_UPLOAD", System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(consent.ConsentStatus, "GRANTED", System.StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(consent => consent.GrantedAt ?? consent.CreatedAt)
            .FirstOrDefault();

        return new VoiceProfileDto(
            profile.Id,
            profile.DisplayName,
            profile.Language,
            profile.Status,
            profile.IsActive,
            profile.VoiceSamples.Any(s => s.DeletedAt == null),
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.Provider,
            profile.EmbeddingRef,
            activeConsent is null ? null : "granted",
            activeConsent?.ConsentTextVersion,
            activeConsent?.GrantedAt
        );
    }
}

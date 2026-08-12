using System.Linq;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class VoiceProfileMapper
{
    public static VoiceProfileDto ToDto(VoiceProfile profile)
    {
        var activeConsent = profile.VoiceConsents
            .Where(consent =>
                consent.ConsentType == VoiceProfileConsentContract.UploadConsentType
                && VoiceProfileConsentContract.IsGranted(consent.ConsentStatus))
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
            // EmbeddingRef is the provider's own reference for the voice. For a picked
            // library voice that is the Cartesia voice id; for a future cloned profile it
            // would be the cloned voice's id. Same column either way.
            profile.EmbeddingRef,
            activeConsent is null ? null : VoiceProfileConsentContract.PublicStatus(activeConsent.ConsentStatus),
            activeConsent?.ConsentTextVersion,
            activeConsent?.GrantedAt
        );
    }
}

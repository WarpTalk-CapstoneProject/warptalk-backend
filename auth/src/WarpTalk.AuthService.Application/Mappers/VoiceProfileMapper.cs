using System.Linq;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class VoiceProfileMapper
{
    public static VoiceProfileDto ToDto(VoiceProfile profile, VoiceConsent? activeConsent = null)
    {
        return new VoiceProfileDto(
            profile.Id,
            profile.DisplayName,
            profile.Language,
            profile.Status,
            profile.IsActive,
            profile.VoiceSamples?.Any(s => s.DeletedAt == null) ?? false,
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.Provider,
            profile.EmbeddingRef,
            activeConsent is null ? null : "granted",
            activeConsent?.ConsentTextVersion,
            activeConsent?.GrantedAt,
            profile.Source,
            // Only a failed row carries a reason; a stale code on a row that has since cloned
            // would put a failure explanation beside a working voice.
            profile.Status == "clone_failed" ? profile.CloneErrorCode : null,
            profile.Status == "clone_failed" ? profile.CloneError : null
        );
    }
}

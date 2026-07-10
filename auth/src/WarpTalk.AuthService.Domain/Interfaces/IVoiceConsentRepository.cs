using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceConsentRepository : IGenericRepository<VoiceConsent>
{
    Task<bool> HasGrantedConsentAsync(Guid voiceProfileId, string consentType, CancellationToken ct = default);
    Task<VoiceConsent?> GetLatestAsync(Guid voiceProfileId, string consentType, ConsentStatus? status = null, CancellationToken ct = default);
}

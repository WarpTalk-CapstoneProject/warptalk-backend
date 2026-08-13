using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceConsentRepository : IGenericRepository<VoiceConsent>
{
    Task<VoiceConsent?> GetCurrentAsync(Guid userId, string consentType, CancellationToken ct = default);

    Task<IReadOnlyList<VoiceConsent>> GetHistoryAsync(Guid userId, string consentType, CancellationToken ct = default);

    void Add(VoiceConsent entity);
}

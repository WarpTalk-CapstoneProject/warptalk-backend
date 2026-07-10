using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceConsentRepository : GenericRepository<VoiceConsent>, IVoiceConsentRepository
{
    public VoiceConsentRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<bool> HasGrantedConsentAsync(Guid voiceProfileId, string consentType, CancellationToken ct = default)
    {
        return await _dbSet.AnyAsync(c =>
            c.VoiceProfileId == voiceProfileId &&
            c.ConsentType == consentType &&
            c.ConsentStatus == ConsentStatus.GRANTED &&
            c.RevokedAt == null,
            ct);
    }

    public async Task<VoiceConsent?> GetLatestAsync(Guid voiceProfileId, string consentType, ConsentStatus? status = null, CancellationToken ct = default)
    {
        var query = _dbSet.Where(c => c.VoiceProfileId == voiceProfileId && c.ConsentType == consentType);

        if (status.HasValue)
        {
            query = query.Where(c => c.ConsentStatus == status.Value);
        }

        return await query
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}

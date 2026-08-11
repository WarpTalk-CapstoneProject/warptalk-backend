using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceConsentRepository : GenericRepository<VoiceConsent>, IVoiceConsentRepository
{
    public VoiceConsentRepository(AuthDbContext context) : base(context)
    {
    }
}

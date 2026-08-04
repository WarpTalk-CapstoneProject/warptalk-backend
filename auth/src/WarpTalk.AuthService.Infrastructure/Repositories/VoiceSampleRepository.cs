using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceSampleRepository : GenericRepository<VoiceSample>, IVoiceSampleRepository
{
    public VoiceSampleRepository(AuthDbContext context) : base(context)
    {
    }
}

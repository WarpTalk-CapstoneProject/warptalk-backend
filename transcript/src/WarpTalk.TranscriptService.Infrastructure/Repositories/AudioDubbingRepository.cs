using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class AudioDubbingRepository : GenericRepository<AudioDubbing>, IAudioDubbingRepository
{
    public AudioDubbingRepository(TranscriptDbContext context) : base(context)
    {
    }
}

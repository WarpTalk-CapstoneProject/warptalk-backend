using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranscriptSegmentRepository : GenericRepository<TranscriptSegment>, ITranscriptSegmentRepository
{
    public TranscriptSegmentRepository(TranscriptDbContext context) : base(context)
    {
    }
}

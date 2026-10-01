using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranscriptCleanSentenceRepository : GenericRepository<TranscriptCleanSentence>, ITranscriptCleanSentenceRepository
{
    public TranscriptCleanSentenceRepository(TranscriptDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<TranscriptCleanSentence>> GetByTranscriptIdAsync(Guid transcriptId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(s => s.TranscriptId == transcriptId)
            .ToListAsync(cancellationToken);
    }
}

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

public class TranscriptPauseWindowRepository : GenericRepository<TranscriptPauseWindow>, ITranscriptPauseWindowRepository
{
    public TranscriptPauseWindowRepository(TranscriptDbContext context) : base(context)
    {
    }

    public async Task<TranscriptPauseWindow?> GetActiveWindowByRoomIdAsync(Guid translationRoomId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(w => w.TranslationRoomId == translationRoomId && w.EndedAt == null)
            .OrderByDescending(w => w.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TranscriptPauseWindow>> GetWindowsByRoomIdAsync(Guid translationRoomId, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(w => w.TranslationRoomId == translationRoomId)
            .OrderBy(w => w.StartedAt)
            .ToListAsync(cancellationToken);
    }
}

using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomArtifactRepository : GenericRepository<TranslationRoomArtifact>, ITranslationRoomArtifactRepository
{
    public TranslationRoomArtifactRepository(TranslationRoomDbContext context) : base(context)
    {
    }

    public async Task<List<TranslationRoomArtifact>> GetArtifactsByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(a => a.TranslationRoomId == roomId && a.DeletedAt == null)
            .ToListAsync(ct);
    }

    public async Task<TranslationRoomArtifact?> GetArtifactWithRoomAsync(Guid artifactId, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(a => a.TranslationRoom)
            .ThenInclude(r => r.TranslationRoomParticipants)
            .FirstOrDefaultAsync(a => a.Id == artifactId && a.DeletedAt == null, ct);
    }
}

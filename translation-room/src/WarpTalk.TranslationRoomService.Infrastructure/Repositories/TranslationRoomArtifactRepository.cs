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

    public async Task<IReadOnlyList<MediaUsageRecording>> GetRecordingsCreatedAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var recording = Domain.Enums.ArtifactType.OPTIONAL_RECORDING.ToString();
        var rows = await (
                from artifact in _dbSet.AsNoTracking()
                join room in _context.TranslationRooms.AsNoTracking() on artifact.TranslationRoomId equals room.Id
                where artifact.ArtifactType == recording
                      && artifact.CreatedAt >= @from
                      && artifact.CreatedAt < to
                select new { room.WorkspaceId, artifact.CreatedAt, artifact.FileSizeBytes })
            .ToListAsync(ct);
        return rows.Select(r => new MediaUsageRecording(r.WorkspaceId, r.CreatedAt, r.FileSizeBytes)).ToList();
    }
}

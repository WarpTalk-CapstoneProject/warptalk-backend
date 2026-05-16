using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomParticipantRepository : GenericRepository<TranslationRoomParticipant>, ITranslationRoomParticipantRepository
{
    public TranslationRoomParticipantRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<TranslationRoomParticipant?> GetByRoomAndUserAsync(Guid roomId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FirstOrDefaultAsync(p => p.TranslationRoomId == roomId && p.UserId == userId, cancellationToken);
    }
}

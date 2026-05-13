using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomParticipantRepository : GenericRepository<TranslationRoomParticipant>, ITranslationRoomParticipantRepository
{
    public TranslationRoomParticipantRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }
}

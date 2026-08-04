using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomInvitationRepository : GenericRepository<TranslationRoomInvitation>, ITranslationRoomInvitationRepository
{
    public TranslationRoomInvitationRepository(TranslationRoomDbContext context) : base(context)
    {
    }
}

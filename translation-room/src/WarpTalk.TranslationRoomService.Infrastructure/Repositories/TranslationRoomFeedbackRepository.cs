using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomFeedbackRepository : GenericRepository<TranslationRoomFeedback>, ITranslationRoomFeedbackRepository
{
    public TranslationRoomFeedbackRepository(TranslationRoomDbContext context) : base(context)
    {
    }
}

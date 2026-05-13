using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomRepository : IGenericRepository<TranslationRoom>
{
    Task<bool> ExistsByCodeAsync(string roomCode, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomSessionRepository : IGenericRepository<TranslationRoomSession>
{
    Task<List<TranslationRoomSession>> GetSessionsByRoomIdAsync(Guid roomId, CancellationToken ct = default);
    Task<TranslationRoomSession?> GetActiveSessionByRoomIdAsync(Guid roomId, CancellationToken ct = default);
}

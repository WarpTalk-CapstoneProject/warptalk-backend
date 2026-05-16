using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomAudioRouteRepository : IGenericRepository<TranslationRoomAudioRoute>
{
    Task<List<TranslationRoomAudioRoute>> GetRoutesByRoomIdAsync(Guid roomId, CancellationToken ct = default);
    Task UpdateRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default);
    Task AddRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default);
}

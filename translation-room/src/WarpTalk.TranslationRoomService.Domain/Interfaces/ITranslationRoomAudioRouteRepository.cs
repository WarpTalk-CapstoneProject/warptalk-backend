using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomAudioRouteRepository : IGenericRepository<TranslationRoomAudioRoute>
{
    Task<List<TranslationRoomAudioRoute>> GetRoutesByRoomIdAsync(Guid roomId, CancellationToken ct = default);
    Task<List<TranslationRoomAudioRoute>> GetRoutesByStatusAsync(AudioRouteStatus status, CancellationToken ct = default);
    Task UpdateRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default);
    Task AddRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default);
    Task RemoveRoutesAsync(IEnumerable<TranslationRoomAudioRoute> routes, CancellationToken ct = default);
}

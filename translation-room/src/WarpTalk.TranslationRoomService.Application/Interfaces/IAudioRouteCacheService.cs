using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IAudioRouteCacheService
{
    Task<List<TranslationRoomAudioRouteDto>> PublishRoutesUpdateAsync(Guid roomId, CancellationToken ct = default);
}

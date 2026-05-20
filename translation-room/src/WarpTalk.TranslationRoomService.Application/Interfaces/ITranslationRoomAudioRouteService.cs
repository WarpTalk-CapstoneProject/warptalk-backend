using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomAudioRouteService
{
    Task<Result<List<TranslationRoomAudioRouteDto>>> GenerateRoutesAsync(Guid roomId, CancellationToken ct = default);
    Task<Result<List<TranslationRoomAudioRouteDto>>> GetRoutesAsync(Guid roomId, CancellationToken ct = default);
    Task<Result<TranslationRoomAudioRouteDto>> UpdateRuntimeContextAsync(Guid roomId, Guid routeId, UpdateAudioRouteRuntimeContextDto dto, CancellationToken ct = default);
    Task<Result<TranslationRoomAudioRouteDto>> ToggleVoiceCloneAsync(Guid roomId, Guid routeId, ToggleVoiceCloneDto dto, CancellationToken ct = default);
}

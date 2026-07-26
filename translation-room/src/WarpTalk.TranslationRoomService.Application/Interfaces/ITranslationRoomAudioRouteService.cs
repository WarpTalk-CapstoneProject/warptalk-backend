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

    /// <summary>
    /// Self-service version of ToggleVoiceCloneAsync — the CALLER (identified by
    /// `userId`, not a specific `routeId`) consents to having their OWN voice cloned,
    /// for every listener they currently have an outgoing route to. Unlike
    /// ToggleVoiceCloneAsync (one route at a time, presumably admin/host-driven),
    /// this is what a participant's own "clone my voice" toggle should call — a
    /// speaker doesn't think in terms of per-listener routes, they think "should the
    /// AI dub of ME use my real voice or not".
    /// </summary>
    Task<Result<List<TranslationRoomAudioRouteDto>>> SetVoiceCloneConsentAsync(Guid roomId, Guid userId, bool enabled, CancellationToken ct = default);
}

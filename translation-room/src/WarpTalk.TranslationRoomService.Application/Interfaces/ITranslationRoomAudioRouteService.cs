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

    /// <summary>
    /// S7 — add only the routes one newly-joined participant needs (them to everyone already
    /// here, and everyone already here to them), leaving every other pair alone.
    ///
    /// Called from the JOIN path, which previously generated no routes at all: routes were
    /// built once inside StartTranslationRoomAsync and never again, so anyone who joined after
    /// Start had no route row — and BaseWorker.is_voice_clone_consented, which matches against
    /// exactly those rows, fails closed. A late joiner permanently got a hashed default voice
    /// instead of their own cloned one.
    ///
    /// Incremental on purpose: the mesh is O(n^2), so calling GenerateRoutesAsync on every join
    /// would re-evaluate every existing pair each time and broadcast a full route update to
    /// every AI worker per joiner.
    /// </summary>
    Task<Result<List<TranslationRoomAudioRouteDto>>> AddRoutesForParticipantAsync(Guid roomId, Guid participantId, CancellationToken ct = default);
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
    /// <summary>
    /// Re-publish this room's routes so a dub-voice change made OUTSIDE this service reaches the
    /// AI pipeline immediately.
    ///
    /// WHY THIS EXISTS AT ALL
    ///     The voice somebody is dubbed in is a user setting and lives in AuthService, which knows
    ///     nothing about rooms. This service learns it only while building a route payload
    ///     (AudioRouteCacheService.WithDubVoicesAsync asks over gRPC on every publish), and the
    ///     workers learn it only from that payload. So a change made mid-meeting was correct in
    ///     AuthService, correct on the voice-profiles page, and invisible to the meeting until
    ///     something else happened to trigger a publish — somebody joining, or translation being
    ///     restarted.
    ///
    /// WHY IT CARRIES NO VOICE ID
    ///     Deliberately. The write already happened in AuthService, and taking the id here as
    ///     well would create a second place the answer is stored and a way for the two to
    ///     disagree. This says only "go and re-read it", and the re-read is the existing one.
    /// </summary>
    Task<Result<List<TranslationRoomAudioRouteDto>>> RefreshDubVoiceAsync(Guid roomId, Guid userId, CancellationToken ct = default);
}

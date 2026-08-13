using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class AudioRouteCacheService : IAudioRouteCacheService
{
    private readonly ITranslationRoomAudioRouteRepository _routeRepository;
    private readonly ITranslationRoomRepository _roomRepository;
    private readonly ITranslationRoomSessionRepository _sessionRepository;
    private readonly IRedisStateRepository _redisStateRepo;

    public AudioRouteCacheService(
        ITranslationRoomAudioRouteRepository routeRepository,
        ITranslationRoomRepository roomRepository,
        ITranslationRoomSessionRepository sessionRepository,
        IRedisStateRepository redisStateRepo)
    {
        _routeRepository = routeRepository;
        _roomRepository = roomRepository;
        _sessionRepository = sessionRepository;
        _redisStateRepo = redisStateRepo;
    }

    public async Task<List<TranslationRoomAudioRouteDto>> PublishRoutesUpdateAsync(Guid roomId, CancellationToken ct = default)
    {
        var allRoutes = await _routeRepository.GetRoutesByRoomIdAsync(roomId, ct);
        var activeOrPendingRoutes = allRoutes
            .Where(r => r.Status != AudioRouteStatus.COMPLETED.ToString())
            .Select(TranslationRoomAudioRouteMapper.ToDto)
            .ToList();

        var room = await _roomRepository.GetByIdAsync(roomId, ct);

        // Whether TRANSLATION is running, as opposed to whether the meeting is open.
        //
        // room_status cannot answer that and was being read as if it could: the AI workers took
        // IN_PROGRESS to mean translation was active, but a room is IN_PROGRESS from the moment
        // somebody opens it, and since WT-339 opening a room deliberately does not start
        // translation. So the pipeline had no way to tell "live meeting, transcript only" from
        // "live meeting, translating", and the two features could not be separated at all.
        //
        // An ACTIVE TranslationRoomSession is the fact itself — it is created by Start Translation
        // and ended by Stop — and it is the same signal the web client reads, so the two cannot
        // drift into disagreeing about whether translation is on.
        var activeSession = await _sessionRepository.GetActiveSessionByRoomIdAsync(roomId, ct);

        // What languages this room was CONFIGURED for, as opposed to which ones happen to
        // be spoken in it right now.
        //
        // The AI pipeline had no access to this and was inferring the room's language set
        // from the participants' speak-languages alone. Those are not the same set: a room
        // configured Vietnamese + Japanese where both people SPEAK Vietnamese and one
        // LISTENS in Japanese produced the set {vi}. STT then treated every Japanese
        // character as foreign to the room and deleted it — a room configured for Japanese,
        // dropping Japanese — while nothing at all constrained the languages it was never
        // told about, which is how Arabic reached that room's transcript.
        //
        // Sent on the payload the STT worker already fetches for room_status, so this costs
        // no extra round trip. `room` is already loaded above.
        var configuredLanguages = new List<string>();
        if (room is not null)
        {
            var source = Helpers.LanguageHelper.NormalizeLanguageCode(room.SourceLanguage);
            if (!string.IsNullOrWhiteSpace(source) && source != "auto")
            {
                configuredLanguages.Add(source);
            }

            foreach (var target in Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages))
            {
                if (!string.IsNullOrWhiteSpace(target) && !configuredLanguages.Contains(target))
                {
                    configuredLanguages.Add(target);
                }
            }
        }

        var payload = new
        {
            routes = activeOrPendingRoutes,
            version = DateTime.UtcNow.Ticks,
            generated_at = DateTime.UtcNow,
            room_status = room?.Status.ToString() ?? string.Empty,
            translation_active = activeSession != null,
            room_languages = configuredLanguages
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        var cacheKey = $"translationRoom:{roomId}:audio_routes";
        var eventChannel = $"translationRoom:{roomId}:events";

        await _redisStateRepo.StringSetAsync(cacheKey, jsonPayload, TimeSpan.FromHours(12));

        var pubSubPayload = JsonSerializer.Serialize(new
        {
            type = "AUDIO_ROUTES_UPDATED",
            data = payload
        });

        await _redisStateRepo.PublishAsync(eventChannel, pubSubPayload);

        return activeOrPendingRoutes;
    }
}

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
    private readonly IRedisStateRepository _redisStateRepo;

    public AudioRouteCacheService(
        ITranslationRoomAudioRouteRepository routeRepository,
        ITranslationRoomRepository roomRepository,
        IRedisStateRepository redisStateRepo)
    {
        _routeRepository = routeRepository;
        _roomRepository = roomRepository;
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
        var payload = new
        {
            routes = activeOrPendingRoutes,
            version = DateTime.UtcNow.Ticks,
            generated_at = DateTime.UtcNow,
            room_status = room?.Status.ToString() ?? string.Empty
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

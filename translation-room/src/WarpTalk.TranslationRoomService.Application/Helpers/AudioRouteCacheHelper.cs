using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using System.Collections.Generic;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class AudioRouteCacheHelper
{
    public static async Task<List<TranslationRoomAudioRouteDto>> PublishRoutesUpdateAsync(
        Guid roomId,
        ITranslationRoomAudioRouteRepository routeRepository,
        ITranslationRoomRepository roomRepository,
        IConnectionMultiplexer redisConnection,
        CancellationToken ct = default)
    {
        var allRoutes = await routeRepository.GetRoutesByRoomIdAsync(roomId, ct);
        var activeOrPendingRoutes = allRoutes
            .Where(r => r.Status != AudioRouteStatus.COMPLETED.ToString())
            .Select(TranslationRoomAudioRouteMapper.ToDto)
            .ToList();

        var room = await roomRepository.GetByIdAsync(roomId, ct);
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

        var db = redisConnection.GetDatabase();
        await db.StringSetAsync(cacheKey, jsonPayload, TimeSpan.FromHours(12));

        var pubSub = redisConnection.GetSubscriber();
        var pubSubPayload = JsonSerializer.Serialize(new
        {
            type = "AUDIO_ROUTES_UPDATED",
            data = payload
        });
        await pubSub.PublishAsync(RedisChannel.Literal(eventChannel), pubSubPayload);

        return activeOrPendingRoutes;
    }
}

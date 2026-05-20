using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.StateMachines;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TelemetryStateService : ITelemetryStateService
{
    private readonly IRedisStateRepository _redisStateRepo;

    public TelemetryStateService(IRedisStateRepository redisStateRepo)
    {
        _redisStateRepo = redisStateRepo;
    }

    public bool IsTelemetryOrTransportEvent(AudioRoutingEventType eventType)
    {
        return eventType == AudioRoutingEventType.token_exhausted ||
               eventType == AudioRoutingEventType.token_recovered ||
               eventType == AudioRoutingEventType.voice_clone_unavailable ||
               eventType == AudioRoutingEventType.voice_clone_recovered ||
               eventType == AudioRoutingEventType.audio_unavailable ||
               eventType == AudioRoutingEventType.audio_recovered ||
               eventType == AudioRoutingEventType.tts_unavailable ||
               eventType == AudioRoutingEventType.telemetry_state_updated;
    }

    public async Task<string> UpdateTransportFlagsAndResolvePayloadAsync(Guid roomId, AudioRoutingEventType eventType)
    {
        var hashKey = CacheKeyHelper.GetTelemetryStateKey(roomId);

        // 1. Map event to corresponding volatile flag updates in Redis
        var updates = new Dictionary<string, string>();
        if (eventType == AudioRoutingEventType.token_exhausted || eventType == AudioRoutingEventType.voice_clone_unavailable)
        {
            updates.Add("voice_clone_status", VoiceCloneStatus.FALLBACK.ToString());
        }
        else if (eventType == AudioRoutingEventType.token_recovered || eventType == AudioRoutingEventType.voice_clone_recovered)
        {
            updates.Add("voice_clone_status", VoiceCloneStatus.NORMAL.ToString());
        }
        else if (eventType == AudioRoutingEventType.audio_unavailable || eventType == AudioRoutingEventType.tts_unavailable)
        {
            updates.Add("delivery_mode", DeliveryMode.TEXT_ONLY.ToString());
        }
        else if (eventType == AudioRoutingEventType.audio_recovered)
        {
            updates.Add("delivery_mode", DeliveryMode.NORMAL.ToString());
        }

        if (updates.Any())
        {
            await _redisStateRepo.HashSetAsync(hashKey, updates);
            await _redisStateRepo.KeyExpireAsync(hashKey, TimeSpan.FromHours(24));
        }

        // 2. Fetch all telemetry flags from Redis to resolve the current unified state
        var stateEntries = await _redisStateRepo.GetHashAllAsync(hashKey);
        bool isSttDegraded = false, isTranslationDegraded = false, isTtsDegraded = false;
        string voiceCloneStatus = VoiceCloneStatus.NORMAL.ToString();
        string deliveryMode = DeliveryMode.NORMAL.ToString();

        if (stateEntries != null)
        {
            if (stateEntries.TryGetValue("is_stt_degraded", out var sttVal))
            {
                isSttDegraded = bool.TryParse(sttVal, out var sttBool) && sttBool;
            }
            if (stateEntries.TryGetValue("is_translation_degraded", out var transVal))
            {
                isTranslationDegraded = bool.TryParse(transVal, out var transBool) && transBool;
            }
            if (stateEntries.TryGetValue("is_tts_degraded", out var ttsVal))
            {
                isTtsDegraded = bool.TryParse(ttsVal, out var ttsBool) && ttsBool;
            }
            if (stateEntries.TryGetValue("voice_clone_status", out var vcVal))
            {
                voiceCloneStatus = vcVal;
            }
            if (stateEntries.TryGetValue("delivery_mode", out var dmVal))
            {
                deliveryMode = dmVal;
            }
        }

        var resolvedStatus = AudioRoutePriorityResolver.ResolveEffectiveStatus(
            isSttDegraded,
            isTranslationDegraded,
            isTtsDegraded,
            voiceCloneStatus,
            deliveryMode);

        return $"{{\"status\":\"{resolvedStatus}\"}}";
    }
}

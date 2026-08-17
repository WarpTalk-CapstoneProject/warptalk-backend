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
               // WT-429. stt_unavailable is the exact sibling of tts_unavailable and belongs on
               // the same path. stt_recovered comes with it because a degraded flag that nothing
               // clears is a worse bug than the one being fixed — it would latch a room into
               // SPEECH_DELAYED for the rest of its life. Its previous route through the state
               // machine was silent acceptance, i.e. a no-op, so this only gives it meaning.
               eventType == AudioRoutingEventType.stt_unavailable ||
               eventType == AudioRoutingEventType.stt_recovered ||
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
        // WT-429. AudioRoutePriorityResolver has always read is_stt_degraded and nothing has ever
        // written it, so the one degradation the resolver was built to weigh could not be set.
        // These two are the only writers, and they are a pair on purpose.
        else if (eventType == AudioRoutingEventType.stt_unavailable)
        {
            updates.Add("is_stt_degraded", "true");
        }
        else if (eventType == AudioRoutingEventType.stt_recovered)
        {
            updates.Add("is_stt_degraded", "false");
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

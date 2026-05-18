using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.StateMachines;

public static class AudioRoutePriorityResolver
{
    public static AudioRouteStatus ResolveEffectiveStatus(
        bool isSttDegraded,
        bool isTranslationDegraded,
        bool isTtsDegraded,
        string voiceCloneStatus,
        string deliveryMode)
    {
        // 1. TEXT_ONLY_MODE - Most severe (audio transport or TTS unavailable)
        if (deliveryMode == "TEXT_ONLY")
        {
            return AudioRouteStatus.TEXT_ONLY_MODE;
        }

        // 2. VOICE_CLONE_FALLBACK - Voice clone failed/exhausted, standard voice fallback
        if (voiceCloneStatus == "FALLBACK")
        {
            return AudioRouteStatus.VOICE_CLONE_FALLBACK;
        }

        // 3. TTS_DEGRADED - TTS pipeline is slow
        if (isTtsDegraded)
        {
            return AudioRouteStatus.TTS_DEGRADED;
        }

        // 4. TRANSLATION_DEGRADED - Translation pipeline is slow
        if (isTranslationDegraded)
        {
            return AudioRouteStatus.TRANSLATION_DEGRADED;
        }

        // 5. STT_DEGRADED - STT pipeline is slow
        if (isSttDegraded)
        {
            return AudioRouteStatus.STT_DEGRADED;
        }

        // 6. Healthy - All stages operational
        return AudioRouteStatus.AUDIO_ROUTING_ACTIVE;
    }
}

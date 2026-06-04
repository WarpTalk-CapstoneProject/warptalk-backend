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
        // 1. CAPTION_ONLY - Most severe: translated audio is unavailable.
        if (deliveryMode == DeliveryMode.TEXT_ONLY.ToString())
        {
            return AudioRouteStatus.CAPTION_ONLY;
        }

        // 2. STANDARD_VOICE - Voice clone failed/exhausted, standard voice fallback.
        if (voiceCloneStatus == VoiceCloneStatus.FALLBACK.ToString())
        {
            return AudioRouteStatus.STANDARD_VOICE;
        }

        // 3. VOICE_DELAYED - TTS or voice output is slow.
        if (isTtsDegraded)
        {
            return AudioRouteStatus.VOICE_DELAYED;
        }

        // 4. TRANSLATION_DELAYED - Translation pipeline is slow.
        if (isTranslationDegraded)
        {
            return AudioRouteStatus.TRANSLATION_DELAYED;
        }

        // 5. SPEECH_DELAYED - Speech recognition is slow.
        if (isSttDegraded)
        {
            return AudioRouteStatus.SPEECH_DELAYED;
        }

        // 6. BROADCASTING - All stages are operational.
        return AudioRouteStatus.BROADCASTING;
    }
}

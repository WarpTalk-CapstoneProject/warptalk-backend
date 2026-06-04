using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomAudioRouteMapper
{
    public static TranslationRoomAudioRoute ToEntity(Guid roomId, TranslationRoomParticipant source, TranslationRoomParticipant target)
    {
        var now = DateTime.UtcNow;
        return new TranslationRoomAudioRoute
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            SourceParticipantId = source.Id,
            TargetParticipantId = target.Id,
            SourceLanguage = source.SpeakLanguage,
            TargetLanguage = target.ListenLanguage,
            VoiceCloneEnabled = false, // Default to false per policy
            Status = AudioRouteStatus.PENDING.ToString(),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static TranslationRoomAudioRouteDto ToDto(TranslationRoomAudioRoute entity)
    {
        return new TranslationRoomAudioRouteDto(
            entity.Id,
            entity.TranslationRoomId,
            entity.SourceParticipantId,
            entity.TargetParticipantId,
            entity.SourceLanguage,
            entity.TargetLanguage,
            entity.VoiceCloneEnabled,
            entity.StreamId,
            ParseStatus(entity.Status),
            entity.StartedAt,
            entity.EndedAt,
            entity.CreatedAt
        );
    }

    private static AudioRouteStatus ParseStatus(string? status)
    {
        if (Enum.TryParse<AudioRouteStatus>(status, true, out var parsed))
        {
            return parsed;
        }

        return status?.ToUpperInvariant() switch
        {
            "IDLE" => AudioRouteStatus.PENDING,
            "ROUTING_READY" => AudioRouteStatus.READY,
            "AUDIO_ROUTING_ACTIVE" => AudioRouteStatus.BROADCASTING,
            "AUDIO_ROUTING_PAUSED" => AudioRouteStatus.PAUSED,
            "STT_DEGRADED" => AudioRouteStatus.SPEECH_DELAYED,
            "TRANSLATION_DEGRADED" => AudioRouteStatus.TRANSLATION_DELAYED,
            "TTS_DEGRADED" => AudioRouteStatus.VOICE_DELAYED,
            "VOICE_CLONE_FALLBACK" => AudioRouteStatus.STANDARD_VOICE,
            "TEXT_ONLY_MODE" => AudioRouteStatus.CAPTION_ONLY,
            "STOPPING" => AudioRouteStatus.ENDING,
            "FINALIZING_ARTIFACTS" => AudioRouteStatus.SAVING_OUTPUTS,
            "FINALIZING_ARTIFACTS_FAILED" => AudioRouteStatus.SAVE_FAILED,
            _ => AudioRouteStatus.PENDING
        };
    }
}

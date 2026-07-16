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
            entity.Status,
            entity.StartedAt,
            entity.EndedAt,
            entity.CreatedAt,
            // Nullable: only populated when the caller eager-loaded SourceParticipant/
            // TargetParticipant (see TranslationRoomAudioRouteRepository.GetRoutesByRoomIdAsync).
            // The AI pipeline (tts_worker) matches its speaker_id (= auth user id) against
            // these, not against SourceParticipantId/TargetParticipantId (translation_room_
            // participants.id) — the two id spaces are different.
            entity.SourceParticipant?.UserId,
            entity.TargetParticipant?.UserId
        );
    }
}

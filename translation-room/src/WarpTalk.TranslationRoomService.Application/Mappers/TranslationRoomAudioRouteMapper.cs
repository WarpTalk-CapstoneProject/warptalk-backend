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
            entity.TargetParticipant?.UserId,
            TextOnly: IsTextOnlyBridgeOutbound(entity)
        );
    }

    /// <summary>
    /// The speaker chose text-only bridge mode and this route goes to the far-side stand-in — i.e.
    /// it is the dub that would have been played into Meet through a cable that does not exist.
    /// Routes to other WarpTalk participants, and the stand-in's own (inbound) routes, are never
    /// text-only. False when the participants were not eager-loaded, which keeps the dub: failing
    /// open costs credits, failing closed would silence a speaker who does have a cable.
    /// </summary>
    public static bool IsTextOnlyBridgeOutbound(TranslationRoomAudioRoute entity) =>
        entity.SourceParticipant?.IsBridgeTextOnly == true
        && entity.TargetParticipant?.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId;
}

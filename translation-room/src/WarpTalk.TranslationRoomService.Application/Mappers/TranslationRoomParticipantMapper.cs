using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomParticipantMapper
{
    public static TranslationRoomParticipant ToParticipantEntity(
        Guid translationRoomId, 
        Guid userId, 
        JoinTranslationRoomRequest request, 
        string speakLanguage,
        string listenLanguage,
        bool requiresApproval,
        bool isHost)
    {
        var role = isHost ? TranslationRoomParticipantRole.HOST : TranslationRoomParticipantRole.PARTICIPANT;
        var initialStatus = (requiresApproval && !isHost) ? TranslationRoomParticipantStatus.WAITING : TranslationRoomParticipantStatus.CONNECTED;

        return new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = translationRoomId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = role.ToString(),
            ListenLanguage = listenLanguage,
            SpeakLanguage = speakLanguage,
            Status = initialStatus.ToString(),
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static void UpdateParticipantEntity(
        TranslationRoomParticipant participant, 
        JoinTranslationRoomRequest request, 
        string speakLanguage, 
        string listenLanguage, 
        bool requiresApproval, 
        bool isHost)
    {
        participant.DisplayName = request.DisplayName;
        participant.ListenLanguage = listenLanguage;
        participant.SpeakLanguage = speakLanguage;
        
        // Recovery logic: If they were DISCONNECTED or LEFT, move to active/pending status
        if (participant.Status == TranslationRoomParticipantStatus.DISCONNECTED.ToString() ||
            participant.Status == TranslationRoomParticipantStatus.LEFT.ToString())
        {
            participant.Status = (requiresApproval && !isHost) 
                ? TranslationRoomParticipantStatus.WAITING.ToString() 
                : TranslationRoomParticipantStatus.CONNECTED.ToString();
        }

        // BR-004: Host check overrides approval
        if (isHost)
        {
            participant.Role = TranslationRoomParticipantRole.HOST.ToString();
            participant.Status = TranslationRoomParticipantStatus.CONNECTED.ToString();
        }
        
        participant.UpdatedAt = DateTime.UtcNow;
    }

    public static TranslationRoomParticipantDto ToParticipantDto(TranslationRoomParticipant participant)
    {
        return new TranslationRoomParticipantDto(
            participant.Id,
            participant.TranslationRoomId,
            participant.UserId,
            participant.DisplayName,
            Enum.Parse<TranslationRoomParticipantRole>(participant.Role, true),
            participant.ListenLanguage,
            participant.SpeakLanguage,
            Enum.Parse<TranslationRoomParticipantStatus>(participant.Status, true),
            participant.IsTranslationAudioEnabled,
            participant.JoinedAt
        );
    }
}

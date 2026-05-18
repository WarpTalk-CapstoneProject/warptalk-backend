using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomParticipantMapper
{
    public static TranslationRoomParticipant ToParticipantEntity(
        this JoinTranslationRoomRequest request,
        Guid translationRoomId, 
        Guid userId, 
        string speakLanguage,
        string listenLanguage,
        bool requiresApproval,
        bool isHost)
    {
        var role = isHost ? nameof(TranslationRoomParticipantRole.HOST) : nameof(TranslationRoomParticipantRole.PARTICIPANT);
        var initialStatus = (requiresApproval && !isHost) 
            ? nameof(TranslationRoomParticipantStatus.WAITING) 
            : nameof(TranslationRoomParticipantStatus.CONNECTED);

        return new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = translationRoomId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = role,
            ListenLanguage = listenLanguage,
            SpeakLanguage = speakLanguage,
            Status = initialStatus,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static void UpdateFrom(
        this TranslationRoomParticipant participant, 
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
        if (participant.Status == nameof(TranslationRoomParticipantStatus.DISCONNECTED) ||
            participant.Status == nameof(TranslationRoomParticipantStatus.LEFT) ||
            participant.Status == nameof(TranslationRoomParticipantStatus.INVITED))
        {
            participant.Status = (requiresApproval && !isHost) 
                ? nameof(TranslationRoomParticipantStatus.WAITING) 
                : nameof(TranslationRoomParticipantStatus.CONNECTED);
        }

        // BR-004: Host check overrides approval
        if (isHost)
        {
            participant.Role = nameof(TranslationRoomParticipantRole.HOST);
            participant.Status = nameof(TranslationRoomParticipantStatus.CONNECTED);
        }
        
        participant.UpdatedAt = DateTime.UtcNow;
    }

    public static TranslationRoomParticipantDto ToDto(this TranslationRoomParticipant participant)
    {
        return new TranslationRoomParticipantDto(
            participant.Id,
            participant.TranslationRoomId,
            participant.UserId.GetValueOrDefault(),
            participant.DisplayName,
            Enum.Parse<TranslationRoomParticipantRole>(participant.Role, true),
            participant.ListenLanguage,
            participant.SpeakLanguage,
            participant.Status,
            participant.IsTranslationAudioEnabled,
            participant.JoinedAt
        );
    }
}

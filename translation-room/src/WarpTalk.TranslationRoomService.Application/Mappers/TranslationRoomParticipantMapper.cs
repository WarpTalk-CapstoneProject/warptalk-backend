using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
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
            ? TranslationRoomParticipantStatuses.Waiting
            : TranslationRoomParticipantStatuses.Connected;

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
        
        // LEFT/DISCONNECTED means this participant was already admitted and later lost or
        // closed their live connection. Admission belongs to the participant's room
        // membership, not to each LiveKit connection, so a reconnect must not send them
        // through the waiting room again. INVITED still represents a first admission.
        if (participant.Status == TranslationRoomParticipantStatuses.Disconnected ||
            participant.Status == TranslationRoomParticipantStatuses.Left)
        {
            participant.Status = TranslationRoomParticipantStatuses.Connected;
            participant.LeftAt = null;
            participant.JoinedAt = DateTime.UtcNow;
        }
        else if (participant.Status == TranslationRoomParticipantStatuses.Invited)
        {
            participant.Status = (requiresApproval && !isHost)
                ? TranslationRoomParticipantStatuses.Waiting
                : TranslationRoomParticipantStatuses.Connected;
        }

        // BR-004: Host check overrides approval
        if (isHost)
        {
            participant.Role = nameof(TranslationRoomParticipantRole.HOST);
            participant.Status = TranslationRoomParticipantStatuses.Connected;
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
            participant.Role,
            participant.ListenLanguage,
            participant.SpeakLanguage,
            participant.Status,
            participant.IsTranslationAudioEnabled,
            participant.JoinedAt
        );
    }
}

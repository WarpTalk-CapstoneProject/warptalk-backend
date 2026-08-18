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
        bool isHost,
        bool isExternal = false)
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
            IsExternal = isExternal,
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
        bool isHost,
        bool isExternal = false)
    {
        participant.DisplayName = request.DisplayName;
        participant.ListenLanguage = listenLanguage;
        participant.SpeakLanguage = speakLanguage;
        // WT-446: refreshed on every admission rather than frozen at the first one. Someone who
        // has since been added to the workspace is no longer a guest, and a roster that kept
        // calling them one would be stating something that stopped being true.
        participant.IsExternal = isExternal;
        
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

        // BR-004: the host never waits in their own lobby, and never loses the room by
        // reconnecting. `isHost` is now the EFFECTIVE host (TranslationRoom.IsHostedBy), so this
        // follows a Transfer Host instead of forever naming whoever booked the room.
        if (isHost)
        {
            participant.Role = nameof(TranslationRoomParticipantRole.HOST);
            participant.Status = TranslationRoomParticipantStatuses.Connected;
        }
        else if (participant.Role == nameof(TranslationRoomParticipantRole.HOST))
        {
            // WT-359: the other half of BR-004, which never existed. Rejoining is the moment a
            // stale HOST row is visible and cheap to correct — the transfer itself demotes the
            // outgoing host, so reaching here means their row predates that fix or was written by
            // a path that bypassed it. Without this the old host walks back in still labelled HOST.
            participant.Role = nameof(TranslationRoomParticipantRole.PARTICIPANT);
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
            participant.JoinedAt,
            participant.IsExternal
        );
    }
}

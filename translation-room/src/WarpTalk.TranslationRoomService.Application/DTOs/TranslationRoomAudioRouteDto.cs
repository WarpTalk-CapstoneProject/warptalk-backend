using System;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record TranslationRoomAudioRouteDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid SourceParticipantId,
    Guid TargetParticipantId,
    string SourceLanguage,
    string TargetLanguage,
    bool VoiceCloneEnabled,
    string? StreamId,
    string Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt,
    Guid? SourceUserId = null,
    Guid? TargetUserId = null
);

using System;
using System.ComponentModel.DataAnnotations;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record GetParticipantsRequest(
    string? Search = null,
    string? Status = null,
    string? Role = null,
    string? SortBy = null,
    bool IsDescending = false
);

public record UpdateParticipantAudioRequest(
    [Required] bool IsTranslationAudioEnabled
);

public record TranslationRoomParticipantDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid UserId,
    string DisplayName,
    TranslationRoomParticipantRole Role,
    string ListenLanguage,
    string SpeakLanguage,
    string Status,
    bool IsTranslationAudioEnabled,
    DateTime? JoinedAt
);

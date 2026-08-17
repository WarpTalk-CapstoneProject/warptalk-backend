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
    string Role,
    string ListenLanguage,
    string SpeakLanguage,
    string Status,
    bool IsTranslationAudioEnabled,
    DateTime? JoinedAt,
    /// <summary>
    /// WT-446: this person was not a member of the room's workspace when they were let in.
    /// The roster labels them so nobody has to guess who is a guest, and usage attribution has a
    /// fact to key on. Trails the record with a default so an older client deserialising this DTO
    /// is unaffected.
    /// </summary>
    bool IsExternal = false
);

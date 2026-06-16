using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record TranslationRoomInvitationDto(
    Guid Id,
    Guid TranslationRoomId,
    string Email,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

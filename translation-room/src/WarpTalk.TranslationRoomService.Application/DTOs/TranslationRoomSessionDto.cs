using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record TranslationRoomSessionDto(
    Guid Id,
    Guid TranslationRoomId,
    string MainLanguage,
    string? AudioUrl,
    string Status,
    DateTime? StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt
);

public record CreateTranslationRoomSessionDto(string MainLanguage);

public record UpdateTranslationRoomSessionDto(string? Status, string? AudioUrl);

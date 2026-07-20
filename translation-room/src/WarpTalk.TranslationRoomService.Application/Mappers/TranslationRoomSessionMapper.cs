using System;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomSessionMapper
{
    public static TranslationRoomSession ToEntity(Guid roomId, CreateTranslationRoomSessionDto dto)
    {
        var now = DateTime.UtcNow;
        return new TranslationRoomSession
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            MainLanguage = dto.MainLanguage,
            Status = TranslationRoomSessionStatus.ACTIVE.ToString(),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static TranslationRoomSessionDto ToDto(TranslationRoomSession entity)
    {
        return new TranslationRoomSessionDto(
            entity.Id,
            entity.TranslationRoomId,
            entity.MainLanguage,
            entity.AudioUrl,
            entity.Status,
            entity.StartedAt,
            entity.EndedAt,
            entity.CreatedAt
        );
    }
}

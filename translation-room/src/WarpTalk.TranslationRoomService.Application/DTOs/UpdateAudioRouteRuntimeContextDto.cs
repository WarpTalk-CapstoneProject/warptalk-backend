using System;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record UpdateAudioRouteRuntimeContextDto(
    string? StreamId,
    AudioRouteStatus? Status
);

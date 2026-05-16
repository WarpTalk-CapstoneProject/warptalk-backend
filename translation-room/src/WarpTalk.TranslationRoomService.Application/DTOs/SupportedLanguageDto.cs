namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record SupportedLanguageDto(
    string Code,
    string Name,
    string? NativeName,
    bool IsActive
);

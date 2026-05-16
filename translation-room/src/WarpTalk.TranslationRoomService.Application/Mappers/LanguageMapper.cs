using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class LanguageMapper
{
    public static SupportedLanguageDto ToDto(string code, string name, string? nativeName, bool isActive)
    {
        return new SupportedLanguageDto(code, name, nativeName, isActive);
    }
}

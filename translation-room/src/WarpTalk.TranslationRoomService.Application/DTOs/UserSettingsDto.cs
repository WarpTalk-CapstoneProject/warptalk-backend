namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record UserSettingsDto(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage
);

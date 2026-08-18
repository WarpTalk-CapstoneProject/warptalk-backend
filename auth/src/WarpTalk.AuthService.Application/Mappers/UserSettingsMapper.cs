using System;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class UserSettingsMapper
{
    /// <summary>
    /// The settings row every account gets at creation.
    ///
    /// The two languages are OPTIONAL OVERRIDES rather than always-defaults, because they are the
    /// only two settings a person is asked for during sign-up. Everything a meeting does with
    /// language starts here — TranslationRoomService reads this row through IUserSettingsDirectory
    /// to pick the speak/listen pair when somebody joins — so leaving them on the constant meant
    /// every new account's first meeting ran in en-US regardless of who they were, and the only
    /// way to find out was to notice it mid-meeting and go looking for a settings page.
    ///
    /// Null or blank falls back to the constant: an invited account created before the sign-up
    /// wizard existed, and the Google path which never asks, must still get a valid row.
    /// </summary>
    public static UserSetting CreateDefaultUserSettings(
        Guid userId,
        string? defaultSpeakLanguage = null,
        string? defaultListenLanguage = null)
    {
        return new UserSetting
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DefaultSpeakLanguage = Normalize(defaultSpeakLanguage) ?? UserConstants.DefaultSpeakLanguage,
            DefaultListenLanguage = Normalize(defaultListenLanguage) ?? UserConstants.DefaultListenLanguage,
            VoiceCloneEnabled = UserConstants.DefaultVoiceCloneEnabled,
            MicNoiseSuppression = UserConstants.DefaultMicNoiseSuppression,
            DefaultTranslationRoomType = UserConstants.DefaultTranslationRoomType,
            AutoRecordTranslationRooms = UserConstants.DefaultAutoRecordTranslationRooms,
            AutoGenerateSummary = UserConstants.DefaultAutoGenerateSummary,
            DefaultMaxParticipants = UserConstants.DefaultMaxParticipants,
            Theme = UserConstants.DefaultTheme,
            TranscriptFontSize = UserConstants.DefaultTranscriptFontSize,
            ShowOriginalTranscript = UserConstants.DefaultShowOriginalTranscript,
            ShowTranslatedTranscript = UserConstants.DefaultShowTranslatedTranscript,
            HighContrast = UserConstants.DefaultHighContrast,
            ScreenReaderMode = UserConstants.DefaultScreenReaderMode,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static string? Normalize(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language.Trim();

    public static UserSettingsDto ToDto(UserSetting s)
    {
        return new UserSettingsDto(
            UserId: s.UserId,
            DefaultSpeakLanguage: s.DefaultSpeakLanguage,
            DefaultListenLanguage: s.DefaultListenLanguage,
            VoiceCloneEnabled: s.VoiceCloneEnabled,
            MicNoiseSuppression: s.MicNoiseSuppression,
            DefaultTranslationRoomType: s.DefaultTranslationRoomType,
            AutoRecordTranslationRooms: s.AutoRecordTranslationRooms,
            AutoGenerateSummary: s.AutoGenerateSummary,
            DefaultMaxParticipants: s.DefaultMaxParticipants,
            Theme: s.Theme,
            TranscriptFontSize: s.TranscriptFontSize,
            ShowOriginalTranscript: s.ShowOriginalTranscript,
            ShowTranslatedTranscript: s.ShowTranslatedTranscript,
            HighContrast: s.HighContrast,
            ScreenReaderMode: s.ScreenReaderMode,
            UpdatedAt: s.UpdatedAt
        );
    }
}

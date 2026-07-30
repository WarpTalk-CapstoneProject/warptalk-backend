using System;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class UserSettingsMapper
{
    public static UserSetting CreateDefaultUserSettings(Guid userId)
    {
        return new UserSetting
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DefaultSpeakLanguage = UserConstants.DefaultSpeakLanguage,
            DefaultListenLanguage = UserConstants.DefaultListenLanguage,
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

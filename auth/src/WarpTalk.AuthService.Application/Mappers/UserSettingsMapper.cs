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
            DefaultSpeakLanguage = AuthConstants.DefaultSpeakLanguage,
            DefaultListenLanguage = AuthConstants.DefaultListenLanguage,
            VoiceCloneEnabled = AuthConstants.DefaultVoiceCloneEnabled,
            MicNoiseSuppression = AuthConstants.DefaultMicNoiseSuppression,
            DefaultTranslationRoomType = AuthConstants.DefaultTranslationRoomType,
            AutoRecordTranslationRooms = AuthConstants.DefaultAutoRecordTranslationRooms,
            AutoGenerateSummary = AuthConstants.DefaultAutoGenerateSummary,
            DefaultMaxParticipants = AuthConstants.DefaultMaxParticipants,
            Theme = AuthConstants.DefaultTheme,
            TranscriptFontSize = AuthConstants.DefaultTranscriptFontSize,
            ShowOriginalTranscript = AuthConstants.DefaultShowOriginalTranscript,
            ShowTranslatedTranscript = AuthConstants.DefaultShowTranslatedTranscript,
            HighContrast = AuthConstants.DefaultHighContrast,
            ScreenReaderMode = AuthConstants.DefaultScreenReaderMode,
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

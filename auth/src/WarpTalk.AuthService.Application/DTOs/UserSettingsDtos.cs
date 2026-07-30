using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record UserSettingsDto(
    Guid UserId,
    string DefaultSpeakLanguage,
    string DefaultListenLanguage,
    bool VoiceCloneEnabled,
    bool MicNoiseSuppression,
    string DefaultTranslationRoomType,
    bool AutoRecordTranslationRooms,
    bool AutoGenerateSummary,
    int DefaultMaxParticipants,
    string Theme,
    int TranscriptFontSize,
    bool ShowOriginalTranscript,
    bool ShowTranslatedTranscript,
    bool HighContrast,
    bool ScreenReaderMode,
    DateTime UpdatedAt
);

public record UpdateUserSettingsRequest(
    string? DefaultSpeakLanguage = null,
    string? DefaultListenLanguage = null,
    bool? VoiceCloneEnabled = null,
    bool? MicNoiseSuppression = null,
    string? DefaultTranslationRoomType = null,
    bool? AutoRecordTranslationRooms = null,
    bool? AutoGenerateSummary = null,
    int? DefaultMaxParticipants = null,
    string? Theme = null,
    int? TranscriptFontSize = null,
    bool? ShowOriginalTranscript = null,
    bool? ShowTranslatedTranscript = null,
    bool? HighContrast = null,
    bool? ScreenReaderMode = null
);


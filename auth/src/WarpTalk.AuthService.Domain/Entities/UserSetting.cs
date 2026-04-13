using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class UserSetting
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DefaultSpeakLanguage { get; set; } = null!;

    public string DefaultListenLanguage { get; set; } = null!;

    public bool VoiceCloneEnabled { get; set; }

    public bool MicNoiseSuppression { get; set; }

    public string DefaultTranslationRoomType { get; set; } = null!;

    public bool AutoRecordTranslationRooms { get; set; }

    public bool AutoGenerateSummary { get; set; }

    public int DefaultMaxParticipants { get; set; }

    public string Theme { get; set; } = null!;

    public int TranscriptFontSize { get; set; }

    public bool ShowOriginalTranscript { get; set; }

    public bool ShowTranslatedTranscript { get; set; }

    public bool CompactParticipantList { get; set; }

    public bool HighContrast { get; set; }

    public bool ScreenReaderMode { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}

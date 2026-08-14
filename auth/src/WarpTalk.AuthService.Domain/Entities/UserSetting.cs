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

    /// <summary>
    /// WT-396. The provider voice id this person is DUBBED IN, or null to clone their voice live
    /// from the meeting — which is what happened to everyone before this existed.
    ///
    /// Not the same direction as a <c>voice.voice_profiles</c> row written by
    /// <c>SetPreferredVoiceAsync</c>: those say which voice this person HEARS other people in.
    /// The two were the same table and a chosen voice went to the wrong one of them.
    /// </summary>
    public string? DubVoiceId { get; set; }

    public bool MicNoiseSuppression { get; set; }

    public string DefaultTranslationRoomType { get; set; } = null!;

    public bool AutoRecordTranslationRooms { get; set; }

    public bool AutoGenerateSummary { get; set; }

    public int DefaultMaxParticipants { get; set; }

    public string Theme { get; set; } = null!;

    public int TranscriptFontSize { get; set; }

    public bool ShowOriginalTranscript { get; set; }

    public bool ShowTranslatedTranscript { get; set; }

    public bool HighContrast { get; set; }

    public bool ScreenReaderMode { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public virtual User? UpdatedByNavigation { get; set; }

    public virtual User? User { get; set; }
}

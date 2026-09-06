using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// WT-605. One [Pause Transcript, Resume Transcript] window for a room's transcript.
///
/// This is DISPLAY METADATA ONLY — the same role <see cref="Guid"/>-keyed
/// TranslationRoomSession plays on the translation-room side (documented there as "the domain
/// model is honestly describable in documentation", not wired into ingestion). The transcript
/// panel uses <see cref="StartedAt"/>/<see cref="EndedAt"/> to draw a "Transcript paused ·
/// HH:MM–HH:MM" divider.
///
/// It is deliberately NOT what TranscriptRedisConsumerService gates persistence on — that gate
/// is a Redis set of skipped segment_ids (translationRoom:{roomId}:transcript_paused_segments),
/// because Translation/TTS result messages carry no absolute timestamp to compare against a
/// window with, only the STT message does (anchor_ms/start_ms). See
/// TranscriptRedisConsumerService.IsRoomTranscriptPausedAsync for the actual gate.
/// </summary>
public partial class TranscriptPauseWindow
{
    public Guid Id { get; set; }

    /// <summary>
    /// External TranslationRoomService room id. No physical FK.
    /// </summary>
    public Guid TranslationRoomId { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>Null while the window is still open (transcript currently paused).</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid PausedBy { get; set; }

    /// <summary>External AuthService user id. No physical FK. Null until resumed.</summary>
    public Guid? ResumedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

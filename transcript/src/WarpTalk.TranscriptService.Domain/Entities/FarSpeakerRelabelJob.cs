using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// One EXTERNAL_BRIDGE room whose far-side segments are to be named after the meeting from Google
/// Meet's own transcript. See migration 20261001120100_add_far_speaker_relabel_jobs.
/// </summary>
public class FarSpeakerRelabelJob
{
    /// <summary>External TranslationRoomService room id. No physical FK.</summary>
    public Guid TranslationRoomId { get; set; }

    /// <summary>One of <see cref="FarSpeakerRelabelJobStatuses"/>.</summary>
    public string Status { get; set; } = FarSpeakerRelabelJobStatuses.Pending;

    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; }

    /// <summary>When the room ended, once known. The 30-day give-up is measured from here.</summary>
    public DateTime? RoomEndedAt { get; set; }

    public string? LastError { get; set; }

    public int? SegmentsRelabeled { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public static class FarSpeakerRelabelJobStatuses
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string NoTranscript = "no_transcript";
    public const string Abandoned = "abandoned";
    public const string Skipped = "skipped";
}

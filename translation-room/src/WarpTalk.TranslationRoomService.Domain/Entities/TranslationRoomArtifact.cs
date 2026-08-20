using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class TranslationRoomArtifact
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string ArtifactType { get; set; } = null!;

    public string? FileUrl { get; set; }

    /// <summary>
    /// Stable provider-side idempotency key, such as the LiveKit Egress id.
    /// </summary>
    public string? ProviderArtifactId { get; set; }

    public string? FileFormat { get; set; }

    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Inline artifact payload (e.g. the AI meeting-summary JSON: overview, decisions,
    /// action items) for artifacts that are small enough to store directly rather than
    /// behind FileUrl. Nullable/optional — most artifact types (transcript export,
    /// recording) keep using FileUrl only.
    /// </summary>
    public string? Content { get; set; }

    public bool ContainsRawAudio { get; set; }

    public bool ContainsRawVideo { get; set; }

    public bool ConsentRequired { get; set; }

    public DateTime? RetentionUntil { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the artifact's CONTENT last changed. Moved by a summary rewrite, which replaces
    /// `Content` in place — without it, "is this summary out of date?" could only ever answer yes,
    /// because regenerating would not clear the comparison.
    ///
    /// NULL for artifacts written before the column existed. Read NULL as UNKNOWN and fall back to
    /// <see cref="CreatedAt"/>; reading it as "never updated" would be a claim.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// WT-473: when the recording BEGAN, in UTC. Null for artifacts that are not recordings, and
    /// for recordings made before this column existed.
    ///
    /// <see cref="CreatedAt"/> is not a substitute: the processor sets it from the event's
    /// OccurredAt, which is the moment egress FINISHED. Seeking a video to a transcript line needs
    /// the recording's own origin, because transcript offsets are measured from the first audio
    /// chunk the STT pipeline saw and a recording starts whenever the host switched it on.
    ///
    /// A null here means NOT SEEKABLE. It must never be read as zero — a historical recording would
    /// then seek to a plausible-looking, silently wrong position on every click.
    /// </summary>
    public DateTime? RecordingStartedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}

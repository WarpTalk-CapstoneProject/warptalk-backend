using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class TranslationRoomArtifact
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string ArtifactType { get; set; } = null!;

    public string? FileUrl { get; set; }

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

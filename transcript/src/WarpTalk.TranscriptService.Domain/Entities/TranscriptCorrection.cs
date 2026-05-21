using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptCorrection
{
    public Guid Id { get; set; }

    public Guid SegmentId { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid UserId { get; set; }

    public string OriginalText { get; set; } = null!;

    public string CorrectedText { get; set; } = null!;

    public bool TriggeredRetranslation { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual TranscriptSegment Segment { get; set; } = null!;
}

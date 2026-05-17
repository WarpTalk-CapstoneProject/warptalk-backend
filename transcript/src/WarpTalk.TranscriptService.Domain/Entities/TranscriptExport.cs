using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptExport
{
    public Guid Id { get; set; }

    public Guid TranscriptId { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid UserId { get; set; }

    public string Format { get; set; } = null!;

    public string FileUrl { get; set; } = null!;

    public string IncludedLanguages { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Transcript Transcript { get; set; } = null!;
}

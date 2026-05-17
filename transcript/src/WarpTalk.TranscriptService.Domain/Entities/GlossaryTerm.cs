using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class GlossaryTerm
{
    public Guid Id { get; set; }

    public Guid GlossaryId { get; set; }

    public string SourceTerm { get; set; } = null!;

    public string TargetTerm { get; set; } = null!;

    public string? Context { get; set; }

    public string? Domain { get; set; }

    public int Priority { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    public virtual Glossary Glossary { get; set; } = null!;
}

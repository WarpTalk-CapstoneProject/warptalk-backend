using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class VoiceProfile
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? WorkspaceId { get; set; }

    public string? DisplayName { get; set; }

    public string? Language { get; set; }

    public string? Provider { get; set; }

    public string? EmbeddingRef { get; set; }

    public string Status { get; set; } = null!;

    /// <summary>
    /// How this voice came to exist — see <see cref="Constants.VoiceProfileSources"/>.
    ///
    /// The two kinds differ in how they may be REPLACED: a captured one is overwritten by a
    /// better capture, an uploaded one never is. Nothing else in this table can tell them apart.
    ///
    /// Defaulted in C# as well as in the column, deliberately. Every existing creation site
    /// builds this entity with an object initialiser and none of them knows about this property;
    /// with <c>null!</c> here EF would send NULL into a NOT NULL column and every upload would
    /// start failing. The column default only covers rows that already existed.
    /// </summary>
    public string Source { get; set; } = Constants.VoiceProfileSources.Upload;

    /// <summary>
    /// Pitch-coverage score (0..1) of the clip this voice was built from, or null when it was
    /// never measured — an upload, or a clone made before this existed.
    ///
    /// NULL is not zero and must never be coerced to it: zero grades as the worst possible
    /// sample and invites replacement by anything at all.
    /// </summary>
    public decimal? QualityScore { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual ICollection<VoiceSample> VoiceSamples { get; set; } = new List<VoiceSample>();

    public virtual ICollection<VoiceConsent> VoiceConsents { get; set; } = new List<VoiceConsent>();
}

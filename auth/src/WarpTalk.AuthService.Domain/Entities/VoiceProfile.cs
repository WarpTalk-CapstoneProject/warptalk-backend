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

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public virtual ICollection<VoiceSample> VoiceSamples { get; set; } = new List<VoiceSample>();
}

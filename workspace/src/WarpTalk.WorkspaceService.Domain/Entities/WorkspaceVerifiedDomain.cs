using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceVerifiedDomain
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Domain { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string VerificationMethod { get; set; } = null!;

    public string VerificationToken { get; set; } = null!;

    public DateTime? VerifiedAt { get; set; }

    public Guid? VerifiedBy { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;
}

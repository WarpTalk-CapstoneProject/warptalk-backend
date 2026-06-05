using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceDocumentAudit
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? ActorId { get; set; }

    public string Action { get; set; } = null!;

    public DateTime ActionAt { get; set; }

    public string? Metadata { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public virtual WorkspaceDocument Document { get; set; } = null!;

    public virtual Workspace Workspace { get; set; } = null!;
}

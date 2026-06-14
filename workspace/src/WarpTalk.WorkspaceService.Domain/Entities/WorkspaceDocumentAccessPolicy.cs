using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceDocumentAccessPolicy
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public Guid WorkspaceId { get; set; }

    public string SubjectType { get; set; } = null!;

    public Guid? SubjectId { get; set; }

    public string? SubjectKey { get; set; }

    public string Permission { get; set; } = null!;

    public string Effect { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual WorkspaceDocument Document { get; set; } = null!;

    public virtual Workspace Workspace { get; set; } = null!;
}

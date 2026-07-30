using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceMember
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public string MembershipType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public bool CanCreateMeetings { get; set; } = true;

    public DateTime JoinedAt { get; set; }

    public DateTime? RemovedAt { get; set; }

    public Guid? RemovedBy { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;
}

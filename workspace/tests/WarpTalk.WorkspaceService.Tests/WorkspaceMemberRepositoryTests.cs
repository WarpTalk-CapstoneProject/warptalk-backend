using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceMemberRepositoryTests
{
    private static WorkspaceMember Row(WorkspaceMemberStatus status, DateTime? removedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        RoleId = Guid.NewGuid(),
        MembershipType = "Internal",
        Status = status.ToStorageValue(),
        JoinedAt = DateTime.UtcNow,
        RemovedAt = removedAt
    };

    private static List<WorkspaceMember> Apply(bool includeInactiveAndBanned, params WorkspaceMember[] rows)
        => rows.Where(WorkspaceMemberRepository.DirectoryVisibilityFilter(includeInactiveAndBanned).Compile()).ToList();

    [Fact]
    public void DirectoryVisibilityFilter_ShouldKeepSuspendedButDropRemoved_WhenViewIsWidened()
    {
        var active = Row(WorkspaceMemberStatus.Active);
        var suspended = Row(WorkspaceMemberStatus.Suspended);
        var removed = Row(WorkspaceMemberStatus.Removed, DateTime.UtcNow);

        var visible = Apply(includeInactiveAndBanned: true, active, suspended, removed);

        Assert.Equal(new[] { active.Id, suspended.Id }, visible.Select(m => m.Id));
    }

    [Fact]
    public void DirectoryVisibilityFilter_ShouldKeepOnlyActive_WhenViewIsNotWidened()
    {
        var active = Row(WorkspaceMemberStatus.Active);
        var suspended = Row(WorkspaceMemberStatus.Suspended);
        var removed = Row(WorkspaceMemberStatus.Removed, DateTime.UtcNow);

        var visible = Apply(includeInactiveAndBanned: false, active, suspended, removed);

        Assert.Equal(new[] { active.Id }, visible.Select(m => m.Id));
    }

    [Fact]
    public void DirectoryVisibilityFilter_ShouldTrustRemovedAt_OverStatus()
    {
        var stampedButStillActive = Row(WorkspaceMemberStatus.Active, DateTime.UtcNow);

        Assert.Empty(Apply(includeInactiveAndBanned: true, stampedButStillActive));
        Assert.Empty(Apply(includeInactiveAndBanned: false, stampedButStillActive));
    }
}

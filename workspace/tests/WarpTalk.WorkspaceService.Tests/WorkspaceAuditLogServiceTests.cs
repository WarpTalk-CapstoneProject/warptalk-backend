using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceAuditLogServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceMemberRepository _members = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IAdminAuditLogRepository _repository = Substitute.For<IAdminAuditLogRepository>();
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly WorkspaceAuditLogService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _ownerRoleId = Guid.NewGuid();
    private readonly Guid _adminRoleId = Guid.NewGuid();
    private readonly Guid _memberRoleId = Guid.NewGuid();

    public WorkspaceAuditLogServiceTests()
    {
        _unitOfWork.WorkspaceMemberRepository.Returns(_members);
        _authIdentity.GetRoleByIdAsync(_ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _ownerRoleId, Name = "Owner" });
        _authIdentity.GetRoleByIdAsync(_adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _adminRoleId, Name = "Admin" });
        _authIdentity.GetRoleByIdAsync(_memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _memberRoleId, Name = "Member" });

        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>(), 0));

        _service = new WorkspaceAuditLogService(
            _unitOfWork, _repository, _authIdentity, Substitute.For<ILogger<WorkspaceAuditLogService>>());
    }

    private void SetupMember(Guid? roleId)
    {
        var member = roleId is null
            ? null
            : new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspaceId,
                UserId = _userId,
                RoleId = roleId.Value,
                MembershipType = MembershipType.Internal.ToString(),
                JoinedAt = DateTime.UtcNow,
            };

        _members.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(member);
    }

    private WorkspaceAdminAction Row(Guid? workspaceId = null, string entityType = AdminAuditEntityTypes.Workspace) => new()
    {
        Id = Guid.NewGuid(),
        SourceService = AdminAuditSources.WorkspaceService,
        Action = "suspend",
        EntityType = entityType,
        EntityId = workspaceId ?? _workspaceId,
        WorkspaceId = workspaceId ?? _workspaceId,
        PerformedBy = Guid.NewGuid(),
        Reason = "Internal fraud signal #42",
        Result = AdminAuditResults.Succeeded,
        PerformedAt = DateTime.UtcNow,
        CorrelationId = "trace-1",
        BeforeSummary = "{\"status\":\"active\"}",
        AfterSummary = "{\"status\":\"suspended\"}",
    };

    [Fact]
    public async Task NonMember_IsForbidden_AndTheLogIsNeverRead()
    {
        SetupMember(null);

        var result = await _service.QueryAsync(_workspaceId, _userId, new WorkspaceAuditLogQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _repository.DidNotReceive().QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlainMember_IsForbidden_AndTheLogIsNeverRead()
    {
        SetupMember(_memberRoleId);

        var result = await _service.QueryAsync(_workspaceId, _userId, new WorkspaceAuditLogQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _repository.DidNotReceive().QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task OwnerAndAdmin_AreAllowed(string role)
    {
        SetupMember(role == "owner" ? _ownerRoleId : _adminRoleId);

        var result = await _service.QueryAsync(_workspaceId, _userId, new WorkspaceAuditLogQuery());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Filter_IsForcedToTheRouteWorkspace_SucceededOnly_AndTenantVisibleCategories()
    {
        SetupMember(_ownerRoleId);

        await _service.QueryAsync(
            _workspaceId,
            _userId,
            new WorkspaceAuditLogQuery { Action = " suspend ", Page = 2, PageSize = 10 });

        await _repository.Received(1).QueryAsync(
            Arg.Is<AdminAuditLogFilter>(f =>
                f.WorkspaceId == _workspaceId
                && f.ActorId == null
                && f.SourceService == null
                && f.Result == AdminAuditResults.Succeeded
                && f.Action == "suspend"
                && f.Page == 2
                && f.PageSize == 10
                && f.EntityTypes != null
                && f.EntityTypes.SequenceEqual(WorkspaceAuditLogService.TenantVisibleEntityTypes)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Entries_RedactActorReasonAndCorrelation_ButKeepSummaries()
    {
        SetupMember(_adminRoleId);
        var row = Row();
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction> { row }, 1));

        var result = await _service.QueryAsync(_workspaceId, _userId, new WorkspaceAuditLogQuery());

        var entry = Assert.Single(result.Value!.Items);
        Assert.Equal(WorkspaceAuditLogService.StaffActorDisplayName, entry.ActorDisplayName);
        Assert.Equal(WorkspaceAuditLogService.StaffActorType, entry.ActorType);
        Assert.Equal("suspended", entry.AfterSummary!["status"]);

        // The DTO has no slot for the staff id, the reason, or the trace id.
        var properties = typeof(WorkspaceAuditLogEntryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("ActorId", properties);
        Assert.DoesNotContain("Reason", properties);
        Assert.DoesNotContain("CorrelationId", properties);
        Assert.DoesNotContain("SourceService", properties);
    }

    [Fact]
    public async Task RowsFromAnotherWorkspaceOrAHiddenCategory_AreDropped_EvenIfTheRepositoryReturnsThem()
    {
        SetupMember(_ownerRoleId);
        var mine = Row();
        _repository.QueryAsync(Arg.Any<AdminAuditLogFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceAdminAction>
            {
                mine,
                Row(workspaceId: Guid.NewGuid()),
                Row(entityType: AdminAuditEntityTypes.User),
            }, 3));

        var result = await _service.QueryAsync(_workspaceId, _userId, new WorkspaceAuditLogQuery());

        var entry = Assert.Single(result.Value!.Items);
        Assert.Equal(mine.Id, entry.Id);
    }

    [Fact]
    public async Task InvertedDateRange_IsAValidationError()
    {
        SetupMember(_ownerRoleId);
        var now = DateTime.UtcNow;

        var result = await _service.QueryAsync(
            _workspaceId, _userId, new WorkspaceAuditLogQuery { From = now, To = now.AddDays(-1) });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public void UserAccountActions_AreNotTenantVisible()
    {
        Assert.DoesNotContain(AdminAuditEntityTypes.User, WorkspaceAuditLogService.TenantVisibleEntityTypes);
        Assert.DoesNotContain(AdminAuditEntityTypes.UsageRate, WorkspaceAuditLogService.TenantVisibleEntityTypes);
        Assert.DoesNotContain(AdminAuditEntityTypes.PricingVersion, WorkspaceAuditLogService.TenantVisibleEntityTypes);
    }
}

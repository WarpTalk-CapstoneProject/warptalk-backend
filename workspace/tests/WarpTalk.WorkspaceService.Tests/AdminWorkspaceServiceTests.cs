using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ReadModels;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AdminWorkspaceServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceRepository _workspaceRepository = Substitute.For<IWorkspaceRepository>();
    private readonly IGenericRepository<WorkspaceAdminAction> _adminActionRepository =
        Substitute.For<IGenericRepository<WorkspaceAdminAction>>();
    private readonly IAuthIdentityClient _authIdentityClient = Substitute.For<IAuthIdentityClient>();
    private readonly AdminWorkspaceService _service;

    public AdminWorkspaceServiceTests()
    {
        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceAdminActionRepository.Returns(_adminActionRepository);
        _adminActionRepository
            .FindAsync(Arg.Any<Expression<Func<WorkspaceAdminAction, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkspaceAdminAction>());

        _service = new AdminWorkspaceService(
            _unitOfWork,
            _authIdentityClient,
            Substitute.For<ILogger<AdminWorkspaceService>>(),
            new FixedTimeProvider(Now));
    }

    private static WorkspaceDirectoryRow Row(
        Guid id,
        Guid ownerId,
        bool isActive = true,
        DateTime? deletedAt = null,
        int memberCount = 3) =>
        new(
            id,
            "Acme Localization",
            "acme-localization",
            null,
            ownerId,
            isActive,
            deletedAt,
            CreatedAt: Now.AddDays(-30),
            UpdatedAt: Now.AddDays(-2),
            AllowExternalCollaboration: true,
            RequireVerifiedDomainForInternal: false,
            MemberCount: memberCount,
            InternalMemberCount: 2,
            ExternalMemberCount: 1,
            PendingInvitationCount: 1,
            DocumentCount: 4,
            VerifiedDomainCount: 1,
            LastMemberJoinedAt: Now.AddDays(-1),
            LastDocumentUploadedAt: Now.AddDays(-5));

    private static Workspace Entity(Guid id, bool isActive = true, DateTime? deletedAt = null) => new()
    {
        Id = id,
        Name = "Acme Localization",
        Slug = "acme-localization",
        OwnerId = Guid.NewGuid(),
        Settings = "{}",
        IsActive = isActive,
        DeletedAt = deletedAt,
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-2),
    };

    // ── Directory ────────────────────────────────────────────

    [Fact]
    public async Task GetDirectoryAsync_RejectsUnknownStatusFilter()
    {
        var result = await _service.GetDirectoryAsync(new AdminWorkspaceDirectoryQuery { Status = "trial" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _workspaceRepository.DidNotReceive()
            .GetAdminDirectoryAsync(Arg.Any<WorkspaceDirectoryFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDirectoryAsync_RejectsUnknownSortKey()
    {
        var result = await _service.GetDirectoryAsync(new AdminWorkspaceDirectoryQuery { Sort = "credits_desc" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task GetDirectoryAsync_RejectsInvertedMemberCountRange()
    {
        var result = await _service.GetDirectoryAsync(
            new AdminWorkspaceDirectoryQuery { MinMembers = 10, MaxMembers = 2 });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task GetDirectoryAsync_ClampsPageSizeAndDefaultsFilter()
    {
        WorkspaceDirectoryFilter? captured = null;
        _workspaceRepository
            .GetAdminDirectoryAsync(Arg.Do<WorkspaceDirectoryFilter>(f => captured = f), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceDirectoryRow>(), 0));

        var result = await _service.GetDirectoryAsync(new AdminWorkspaceDirectoryQuery { Page = 0, PageSize = 5000 });

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(1, captured!.Page);
        Assert.Equal(100, captured.PageSize);
        Assert.Equal(WorkspaceLifecycleStatus.All, captured.Status);
        Assert.Equal(WorkspaceDirectorySort.CreatedDesc, captured.Sort);
    }

    [Fact]
    public async Task GetDirectoryAsync_ResolvesEachOwnerOnceAndMapsSummaries()
    {
        var sharedOwnerId = Guid.NewGuid();
        var rows = new List<WorkspaceDirectoryRow>
        {
            Row(Guid.NewGuid(), sharedOwnerId),
            Row(Guid.NewGuid(), sharedOwnerId, isActive: false),
        };
        _workspaceRepository
            .GetAdminDirectoryAsync(Arg.Any<WorkspaceDirectoryFilter>(), Arg.Any<CancellationToken>())
            .Returns((rows, 2));
        _authIdentityClient.GetUserByIdAsync(sharedOwnerId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = sharedOwnerId, FullName = "Mai Tran", Email = "mai@acme.com" });

        var result = await _service.GetDirectoryAsync(new AdminWorkspaceDirectoryQuery());

        Assert.True(result.IsSuccess);
        var items = result.Value!.Items;
        Assert.Equal(2, result.Value.Total);
        Assert.Equal(WorkspaceLifecycleStatus.Active, items[0].Status);
        Assert.Equal(WorkspaceLifecycleStatus.Suspended, items[1].Status);
        Assert.Equal("Mai Tran", items[0].Owner.FullName);
        Assert.True(items[0].Owner.Resolved);
        await _authIdentityClient.Received(1).GetUserByIdAsync(sharedOwnerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDirectoryAsync_ReportsUnresolvedOwnerInsteadOfInventingOne()
    {
        var ownerId = Guid.NewGuid();
        _workspaceRepository
            .GetAdminDirectoryAsync(Arg.Any<WorkspaceDirectoryFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<WorkspaceDirectoryRow> { Row(Guid.NewGuid(), ownerId) }, 1));
        _authIdentityClient.GetUserByIdAsync(ownerId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _service.GetDirectoryAsync(new AdminWorkspaceDirectoryQuery());

        var owner = result.Value!.Items[0].Owner;
        Assert.False(owner.Resolved);
        Assert.Null(owner.FullName);
        Assert.Equal(ownerId, owner.Id);
    }

    // ── Detail ───────────────────────────────────────────────

    [Fact]
    public async Task GetDetailAsync_ReturnsNotFoundForUnknownWorkspace()
    {
        _workspaceRepository.GetAdminDetailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceDirectoryRow?)null);

        var result = await _service.GetDetailAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task GetDetailAsync_ReportsDeletedAheadOfSuspended()
    {
        var id = Guid.NewGuid();
        _workspaceRepository.GetAdminDetailAsync(id, Arg.Any<CancellationToken>())
            .Returns(Row(id, Guid.NewGuid(), isActive: true, deletedAt: Now.AddDays(-1)));

        var result = await _service.GetDetailAsync(id);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceLifecycleStatus.Deleted, result.Value!.Status);
    }

    [Fact]
    public async Task GetDetailAsync_ExposesCurrentSuspensionOnlyWhileSuspended()
    {
        var id = Guid.NewGuid();
        var suspendAction = new WorkspaceAdminAction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = id,
            Action = WorkspaceAdminActionTypes.Suspend,
            Reason = "Payment overdue",
            PerformedBy = Guid.NewGuid(),
            PerformedAt = Now.AddDays(-3),
        };
        var reactivateAction = new WorkspaceAdminAction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = id,
            Action = WorkspaceAdminActionTypes.Reactivate,
            Reason = "Invoice settled",
            PerformedBy = Guid.NewGuid(),
            PerformedAt = Now.AddDays(-1),
        };
        _adminActionRepository
            .FindAsync(Arg.Any<Expression<Func<WorkspaceAdminAction, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { suspendAction, reactivateAction });
        _workspaceRepository.GetAdminDetailAsync(id, Arg.Any<CancellationToken>())
            .Returns(Row(id, Guid.NewGuid(), isActive: true));

        var result = await _service.GetDetailAsync(id);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.CurrentSuspension);
        Assert.Equal(2, result.Value.LifecycleHistory.Count);
        Assert.Equal(WorkspaceAdminActionTypes.Reactivate, result.Value.LifecycleHistory[0].Action);
    }

    // ── Lifecycle ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SuspendAsync_RequiresAReason(string reason)
    {
        var result = await _service.SuspendAsync(Guid.NewGuid(), reason, Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _workspaceRepository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendAsync_RejectsOverlongReason()
    {
        var result = await _service.SuspendAsync(
            Guid.NewGuid(), new string('x', 501), Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SuspendAsync_ReturnsNotFoundForUnknownWorkspace()
    {
        _workspaceRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Workspace?)null);

        var result = await _service.SuspendAsync(Guid.NewGuid(), "Abuse report", Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task SuspendAsync_ConflictsWhenAlreadySuspended()
    {
        var id = Guid.NewGuid();
        _workspaceRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Entity(id, isActive: false));

        var result = await _service.SuspendAsync(id, "Abuse report", Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendAsync_ConflictsForSoftDeletedWorkspace()
    {
        var id = Guid.NewGuid();
        _workspaceRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Entity(id, isActive: true, deletedAt: Now.AddDays(-4)));

        var result = await _service.SuspendAsync(id, "Abuse report", Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task SuspendAsync_FlipsIsActiveAndAppendsAuditRowWithoutDeletingData()
    {
        var id = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var workspace = Entity(id);
        _workspaceRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceRepository.GetAdminDetailAsync(id, Arg.Any<CancellationToken>())
            .Returns(Row(id, workspace.OwnerId, isActive: false));

        WorkspaceAdminAction? appended = null;
        await _adminActionRepository.AddAsync(
            Arg.Do<WorkspaceAdminAction>(a => appended = a), Arg.Any<CancellationToken>());

        var result = await _service.SuspendAsync(id, "  Abuse report  ", actorId, "trace-42");

        Assert.True(result.IsSuccess);
        Assert.False(workspace.IsActive);
        Assert.Null(workspace.DeletedAt);
        Assert.Equal("Acme Localization", workspace.Name);
        Assert.Equal(actorId, workspace.UpdatedBy);
        Assert.Equal(Now, workspace.UpdatedAt);

        Assert.NotNull(appended);
        Assert.Equal(WorkspaceAdminActionTypes.Suspend, appended!.Action);
        Assert.Equal("Abuse report", appended.Reason);
        Assert.Equal(actorId, appended.PerformedBy);
        Assert.Equal(Now, appended.PerformedAt);
        Assert.Equal("trace-42", appended.CorrelationId);

        _workspaceRepository.Received(1).Update(workspace);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReactivateAsync_ConflictsWhenAlreadyActive()
    {
        var id = Guid.NewGuid();
        _workspaceRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Entity(id, isActive: true));

        var result = await _service.ReactivateAsync(id, "Invoice settled", Guid.NewGuid(), null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task ReactivateAsync_RestoresIsActiveAndAppendsASecondAuditRow()
    {
        var id = Guid.NewGuid();
        var workspace = Entity(id, isActive: false);
        var existingSuspend = new WorkspaceAdminAction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = id,
            Action = WorkspaceAdminActionTypes.Suspend,
            Reason = "Abuse report",
            PerformedBy = Guid.NewGuid(),
            PerformedAt = Now.AddDays(-2),
        };
        _adminActionRepository
            .FindAsync(Arg.Any<Expression<Func<WorkspaceAdminAction, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existingSuspend });
        _workspaceRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceRepository.GetAdminDetailAsync(id, Arg.Any<CancellationToken>())
            .Returns(Row(id, workspace.OwnerId, isActive: true));

        var result = await _service.ReactivateAsync(id, "Invoice settled", Guid.NewGuid(), null);

        Assert.True(result.IsSuccess);
        Assert.True(workspace.IsActive);
        await _adminActionRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceAdminAction>(a =>
                a.Action == WorkspaceAdminActionTypes.Reactivate && a.Reason == "Invoice settled"),
            Arg.Any<CancellationToken>());
        // The earlier suspend row is never touched: history is append-only.
        Assert.Equal("Abuse report", existingSuspend.Reason);
        Assert.Equal(WorkspaceAdminActionTypes.Suspend, existingSuspend.Action);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}

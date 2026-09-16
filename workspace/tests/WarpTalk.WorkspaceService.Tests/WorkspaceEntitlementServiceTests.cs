using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The workspace Features page reads the replicated entitlement snapshot. It must report what
/// billing resolved — provenance and the owner's ceiling included — and never invent values when
/// no snapshot exists.
/// </summary>
public class WorkspaceEntitlementServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceMemberRepository _members = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IWorkspaceEntitlementSnapshotRepository _snapshots =
        Substitute.For<IWorkspaceEntitlementSnapshotRepository>();

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public WorkspaceEntitlementServiceTests()
    {
        _unitOfWork.WorkspaceRepository.Returns(_workspaces);
        _unitOfWork.WorkspaceMemberRepository.Returns(_members);
        _unitOfWork.WorkspaceEntitlementSnapshotRepository.Returns(_snapshots);
        _workspaces.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = _workspaceId });
    }

    private WorkspaceEntitlementService Build() =>
        new(_unitOfWork, NullLogger<WorkspaceEntitlementService>.Instance);

    private void AsMember() =>
        _members.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = _workspaceId, UserId = _userId });

    [Fact]
    public async Task ANonMemberIsRefused()
    {
        _members.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        var result = await Build().GetEntitlementsAsync(_workspaceId, _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _snapshots.DidNotReceive().GetForWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeletedWorkspaceIsNotFound_EvenForALingeringMembership()
    {
        AsMember();
        _workspaces.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = _workspaceId, DeletedAt = DateTime.UtcNow });

        var result = await Build().GetEntitlementsAsync(_workspaceId, _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ColdStart_IsReportedAsUnknown_NotAsPlatformDefaults()
    {
        AsMember();
        _snapshots.GetForWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceEntitlementSnapshot?)null);

        var result = await Build().GetEntitlementsAsync(_workspaceId, _userId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsKnown);
        Assert.Empty(result.Value.Entitlements);
        Assert.Null(result.Value.ResolvedAt);
    }

    [Fact]
    public async Task TheSnapshotIsReportedVerbatim_WithKindAndTheOwnersCeiling()
    {
        AsMember();
        var resolvedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        _snapshots.GetForWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntitlementSnapshot
            {
                WorkspaceId = _workspaceId,
                PlanSlug = "enterprise",
                HasActiveSubscription = true,
                ResolvedAt = resolvedAt,
                EntitlementsJson =
                    "{\"max_languages\":{\"value\":\"10\",\"source\":\"plan:enterprise\"}," +
                    "\"max_active_rooms\":{\"value\":\"5\",\"source\":\"workspace_override\",\"ceiling\":\"20\",\"ceiling_source\":\"plan:enterprise\"}," +
                    "\"voice_clone\":{\"value\":\"true\",\"source\":\"contract_override\"}}"
            });

        var result = await Build().GetEntitlementsAsync(_workspaceId, _userId);

        Assert.True(result.IsSuccess);
        var dto = result.Value!;
        Assert.True(dto.IsKnown);
        Assert.Equal("enterprise", dto.PlanSlug);
        Assert.True(dto.HasActiveSubscription);
        Assert.Equal(resolvedAt, dto.ResolvedAt);
        Assert.Equal(3, dto.Entitlements.Count);

        var languages = Assert.Single(dto.Entitlements, e => e.Key == "max_languages");
        Assert.Equal(("limit", "10", "plan:enterprise"), (languages.Kind, languages.Value, languages.Source));
        Assert.Null(languages.Ceiling);

        var rooms = Assert.Single(dto.Entitlements, e => e.Key == "max_active_rooms");
        Assert.Equal(("5", "workspace_override"), (rooms.Value, rooms.Source));
        Assert.Equal(("20", "plan:enterprise"), (rooms.Ceiling, rooms.CeilingSource));

        var clone = Assert.Single(dto.Entitlements, e => e.Key == "voice_clone");
        Assert.Equal(("flag", "true", "contract_override"), (clone.Kind, clone.Value, clone.Source));
    }

    [Fact]
    public async Task AnOverrideFromBeforeCeilingsWerePublished_HasNoCeiling_RatherThanAGuess()
    {
        AsMember();
        _snapshots.GetForWorkspaceAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntitlementSnapshot
            {
                WorkspaceId = _workspaceId,
                HasActiveSubscription = true,
                ResolvedAt = DateTime.UtcNow,
                EntitlementsJson = "{\"max_active_rooms\":{\"value\":\"5\",\"source\":\"workspace_override\"}}"
            });

        var result = await Build().GetEntitlementsAsync(_workspaceId, _userId);

        var rooms = Assert.Single(result.Value!.Entitlements);
        Assert.Null(rooms.Ceiling);
        Assert.Null(rooms.CeilingSource);
    }

    [Theory]
    [InlineData("true", "flag")]
    [InlineData("false", "flag")]
    [InlineData("0", "limit")]
    [InlineData("250", "limit")]
    [InlineData("gpt", "text")]
    public void KindIsDecidedByTheValueShape(string value, string kind) =>
        Assert.Equal(kind, WorkspaceEntitlementService.KindOf(value));
}

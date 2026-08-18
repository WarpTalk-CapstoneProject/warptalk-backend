using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using Xunit;
using AppWorkspaceService = WarpTalk.WorkspaceService.Application.Services.WorkspaceService;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-430. A settings value billing REFUSES must not survive in the database.
///
/// The save used to write the settings JSON and commit it, and only then ask billing whether the
/// number was allowed. When billing said no, the caller got a failure and the row kept the value:
/// production carried a stored MaxActiveRooms of 20 against a ceiling of 5, and the enforcement
/// error had to quote both — "the workspace setting of 20 cannot raise it — only lower it."
///
/// The existing WorkspaceServiceTests fixture constructs the service with no billing client at all,
/// so its branch never ran there. That is why nothing caught this.
/// </summary>
public class WorkspaceSettingsEntitlementOrderTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceRepository _workspaceRepository = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceMemberRepository _memberRepository = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly IBillingSubscriptionClient _billing = Substitute.For<IBillingSubscriptionClient>();

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _ownerRoleId = Guid.NewGuid();

    private AppWorkspaceService Build()
    {
        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_memberRepository);

        _workspaceRepository.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>()).Returns(new Workspace
        {
            Id = _workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"AllowExternalCollaboration\":true,\"RequireVerifiedDomainForInternal\":false,\"ArtifactRetentionDays\":30}"
        });
        _memberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember
            {
                Id = Guid.NewGuid(), WorkspaceId = _workspaceId, UserId = _userId, RoleId = _ownerRoleId
            });
        _authIdentity.GetRoleByIdAsync(_ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _ownerRoleId, Name = "Owner" });
        _workspaceRepository.UpdateSettingsAsync(
                Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        return new AppWorkspaceService(
            _unitOfWork,
            Substitute.For<IWorkspaceCacheService>(),
            Substitute.For<ILogger<AppWorkspaceService>>(),
            _authIdentity,
            Substitute.For<IWorkspaceEventPublisher>(),
            _billing);
    }

    private static WorkspaceSettingsDto Settings(int maxActiveRooms) => new(
        "en", "UTC", new List<string>(), true, maxActiveRooms, 30,
        new List<string>(), true, false, null, false);

    [Fact]
    public async Task ARefusedValueIsNeverWrittenToTheWorkspace()
    {
        _billing
            .ApplyWorkspaceEntitlementOverridesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Workspace setting 'max_active_rooms' cannot exceed what the plan allows (5).");

        var result = await Build().UpdateWorkspaceSettingsAsync(_workspaceId, Settings(20), _userId);

        Assert.False(result.IsSuccess);
        Assert.Contains("cannot exceed", result.Error);

        // The point of the fix: no write, and nothing committed.
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAcceptedValueIsWritten()
    {
        _billing
            .ApplyWorkspaceEntitlementOverridesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await Build().UpdateWorkspaceSettingsAsync(_workspaceId, Settings(20), _userId);

        Assert.True(result.IsSuccess);
        await _workspaceRepository.Received(1).UpdateSettingsAsync(
            _workspaceId, Arg.Any<WorkspaceConfiguration>(), _userId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BillingIsAskedBeforeTheWriteHappens()
    {
        // Asserted as an ORDER, not just an outcome: a future edit could reinstate the write-first
        // sequence and still pass the two tests above by happening to return success.
        var callOrder = new List<string>();

        // Built FIRST: Build() stubs UpdateSettingsAsync itself, so recording stubs set before it
        // would be overwritten and the write would never be observed.
        var service = Build();

        _billing
            .ApplyWorkspaceEntitlementOverridesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("billing"); return (string?)null; });
        _workspaceRepository
            .UpdateSettingsAsync(
                Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("write"); return true; });

        await service.UpdateWorkspaceSettingsAsync(_workspaceId, Settings(3), _userId);

        Assert.Equal(new[] { "billing", "write" }, callOrder);
    }

    [Fact]
    public async Task AnOutageStillLetsTheOwnerSave()
    {
        // The client returns null (accepted) when billing is unreachable, by design — this is a
        // tightening-only write, so an outage cannot grant anybody anything, and an owner must not
        // be locked out of their own settings because billing is down.
        _billing
            .ApplyWorkspaceEntitlementOverridesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await Build().UpdateWorkspaceSettingsAsync(_workspaceId, Settings(2), _userId);

        Assert.True(result.IsSuccess);
    }
}

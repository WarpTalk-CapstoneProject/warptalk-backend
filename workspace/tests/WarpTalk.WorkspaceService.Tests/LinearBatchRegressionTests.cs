using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;
using AppWorkspaceMemberService = WarpTalk.WorkspaceService.Application.Services.WorkspaceMemberService;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using SendNotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;
using NotificationResponse = WarpTalk.Shared.Protos.SendNotificationResponse;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Regressions for the Linear batch of 2026-08-16: WT-434 (delete workspace 400) and
/// WT-431 (role change rings no bell). Each test names the production behaviour it pins.
/// </summary>
public class LinearBatchRegressionTests
{
    // ---------------------------------------------------------------- WT-434

    /// <summary>
    /// GetActiveMembersByWorkspaceAsync is AsNoTracking, so in production it returns FRESH
    /// instances — not the tracked object the earlier FirstOrDefaultAsync returned for the
    /// executing owner. Update() on that detached twin threw EF's identity-map exception, the
    /// catch turned it into UnexpectedError, and the controller mapped it to 400. The older
    /// delete test cannot see this because its mock hands back the SAME instance for both reads;
    /// this one returns a detached copy the way the real repository does.
    /// </summary>
    [Fact]
    public async Task WT434_DeleteStampsTheTrackedOwnerInstance_NotADetachedTwin()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var memberRepo = Substitute.For<IWorkspaceMemberRepository>();
        var authIdentity = Substitute.For<IAuthIdentityClient>();
        unitOfWork.WorkspaceRepository.Returns(workspaceRepo);
        unitOfWork.WorkspaceMemberRepository.Returns(memberRepo);

        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var rowId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId, OwnerId = ownerUserId };
        var trackedOwner = new WorkspaceMember { Id = rowId, WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        // The AsNoTracking copy: same key, DIFFERENT instance — production's shape.
        var detachedOwner = new WorkspaceMember { Id = rowId, WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        workspaceRepo.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        memberRepo.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(trackedOwner);
        authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        memberRepo.GetActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { detachedOwner });

        var updated = new List<WorkspaceMember>();
        memberRepo.When(r => r.Update(Arg.Any<WorkspaceMember>()))
            .Do(call => updated.Add(call.Arg<WorkspaceMember>()));

        var service = new WarpTalk.WorkspaceService.Application.Services.WorkspaceService(
            unitOfWork,
            Substitute.For<IWorkspaceCacheService>(),
            Substitute.For<ILogger<WarpTalk.WorkspaceService.Application.Services.WorkspaceService>>(),
            authIdentity,
            Substitute.For<IWorkspaceEventPublisher>(),
            Substitute.For<IBillingSubscriptionClient>());

        var result = await service.SoftDeleteWorkspaceAsync(workspaceId, ownerUserId);

        Assert.True(result.IsSuccess, result.Error);
        // The tracked instance is the one stamped and updated; passing the detached twin to
        // Update() is precisely what threw in production.
        Assert.Contains(trackedOwner, updated);
        Assert.DoesNotContain(detachedOwner, updated);
        Assert.NotNull(trackedOwner.RemovedAt);
    }

    // ---------------------------------------------------------------- WT-431

    /// <summary>Hand-written fake — the generated gRPC client's methods are virtual.</summary>
    private sealed class RecordingNotificationClient : NotificationClient
    {
        public readonly List<SendNotificationRequest> Sent = new();
        public bool Fail;

        public override AsyncUnaryCall<NotificationResponse> SendNotificationAsync(
            SendNotificationRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (Fail) throw new RpcException(new Status(StatusCode.Unavailable, "mesh down"));
            Sent.Add(request);
            return new AsyncUnaryCall<NotificationResponse>(
                Task.FromResult(new NotificationResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed record RoleChangeWorld(
        AppWorkspaceMemberService Service,
        RecordingNotificationClient Bell,
        WorkspaceMember Target,
        Guid OwnerUserId);

    private static RoleChangeWorld RoleChangeFixture()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var workspaceRepo = Substitute.For<IWorkspaceRepository>();
        var memberRepo = Substitute.For<IWorkspaceMemberRepository>();
        var authIdentity = Substitute.For<IAuthIdentityClient>();
        unitOfWork.WorkspaceRepository.Returns(workspaceRepo);
        unitOfWork.WorkspaceMemberRepository.Returns(memberRepo);

        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = memberRoleId, MembershipType = "Internal" };

        workspaceRepo.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, Name = "kim", Slug = "kim" });
        memberRepo.FirstOrDefaultAsync(
                Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(owner)),
                "", Arg.Any<CancellationToken>())
            .Returns(owner);
        memberRepo.FirstOrDefaultAsync(
                Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(target)),
                "", Arg.Any<CancellationToken>())
            .Returns(target);
        authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Member" });
        authIdentity.GetRoleByNameAsync("Admin", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminRoleId, Name = "Admin" });

        var bell = new RecordingNotificationClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RolePreview:SigningKey"] = "test-role-preview-signing-key-with-at-least-32-characters",
            })
            .Build();

        var service = new AppWorkspaceMemberService(
            unitOfWork,
            Substitute.For<ILogger<AppWorkspaceMemberService>>(),
            authIdentity,
            Substitute.For<IWorkspaceEventPublisher>(),
            configuration,
            bell);

        return new RoleChangeWorld(service, bell, target, ownerUserId);
    }

    [Fact]
    public async Task WT431_TheAffectedMemberIsNotified_AfterTheChangeCommits()
    {
        var world = RoleChangeFixture();

        var result = await world.Service.ChangeMemberRoleAsync(
            world.Target.WorkspaceId, world.Target.UserId, "Admin", world.OwnerUserId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var sent = Assert.Single(world.Bell.Sent);
        Assert.Equal(world.Target.UserId.ToString(), sent.UserId);
        Assert.Equal("WORKSPACE_ROLE_CHANGED", sent.Type);
        Assert.Contains("Admin", sent.Body);
        Assert.Contains("Member", sent.Body);
        Assert.Equal("Admin", sent.Metadata["new_role"]);
    }

    [Fact]
    public async Task WT431_AnUnreachableNotificationMesh_DoesNotFailTheRoleChange()
    {
        var world = RoleChangeFixture();
        world.Bell.Fail = true;

        var result = await world.Service.ChangeMemberRoleAsync(
            world.Target.WorkspaceId, world.Target.UserId, "Admin", world.OwnerUserId, CancellationToken.None);

        // The change is committed before the bell rings; a dead mesh must cost the announcement
        // only — which is exactly the pre-fix behaviour, not a new failure.
        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(world.Bell.Sent);
    }

    [Fact]
    public async Task WT431_ANoopRoleChange_StaysSilent()
    {
        var world = RoleChangeFixture();

        // Target already holds Member; asking for Member again early-returns before the commit.
        var result = await world.Service.ChangeMemberRoleAsync(
            world.Target.WorkspaceId, world.Target.UserId, "Member", world.OwnerUserId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(world.Bell.Sent);
    }
}

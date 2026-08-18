using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Interfaces;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceLeaveRequestServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceInvitationRepository _workspaceInvitationRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceInvitationEmailComposer _emailComposer;
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;
    private readonly IWorkspaceInvitationAcceptanceProcessor _acceptanceProcessor;
    private readonly WorkspaceInvitationService _service;

    public WorkspaceLeaveRequestServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceInvitationRepository = Substitute.For<IWorkspaceInvitationRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _emailComposer = Substitute.For<IWorkspaceInvitationEmailComposer>();
        _billingSubscriptionClient = Substitute.For<IBillingSubscriptionClient>();
        _acceptanceProcessor = Substitute.For<IWorkspaceInvitationAcceptanceProcessor>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceInvitationRepository.Returns(_workspaceInvitationRepository);

        _service = new WorkspaceInvitationService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceInvitationService>>(),
            _authIdentity,
            _translationRoomClient,
            _emailComposer,
            _billingSubscriptionClient,
            _acceptanceProcessor);
    }

    [Fact]
    public async Task CreateLeaveRequest_ShouldSucceed_WhenValidMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var userEmail = "member@example.com";

        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = roleId, Name = "Member" });
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>()).Returns(new Role { Id = roleId, Name = "Member" });

        // Act
        var result = await _service.CreateLeaveRequestAsync(workspaceId, userId, userEmail);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(workspaceId, result.Value.WorkspaceId);
        await _workspaceInvitationRepository.Received(1).AddAsync(Arg.Is<WorkspaceInvitation>(i => i.Status == "LEAVE_REQUESTED"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLeaveRequest_ShouldFail_WhenSoleOwnerAttemptsToLeave()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var userEmail = "owner@example.com";

        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = ownerRoleId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        _authIdentity.GetRoleByNameAsync("Owner", Arg.Any<CancellationToken>()).Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId, Arg.Any<CancellationToken>()).Returns(1);

        // Act
        var result = await _service.CreateLeaveRequestAsync(workspaceId, userId, userEmail);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotLeaveAsLastOwner, result.Error);
    }

    [Fact]
    public async Task ApproveLeaveRequest_ShouldSucceed_WhenAdminApproves()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var leaveRequestId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var leaveRequest = new WorkspaceInvitation { Id = leaveRequestId, WorkspaceId = workspaceId, RequestedBy = targetUserId, Status = "LEAVE_REQUESTED" };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = Guid.NewGuid() };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(adminMember, targetMember);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = adminRoleId, Name = "Admin" });
        _workspaceInvitationRepository.GetByIdAsync(leaveRequestId, Arg.Any<CancellationToken>()).Returns(leaveRequest);

        // Act
        var result = await _service.ApproveLeaveRequestAsync(workspaceId, leaveRequestId, adminUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), leaveRequest.Status);
        Assert.NotNull(targetMember.RemovedAt);
        Assert.Equal(adminUserId, targetMember.RemovedBy);
    }

    [Fact]
    public async Task RejectLeaveRequest_ShouldSucceed_WhenAdminRejects()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var leaveRequestId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var leaveRequest = new WorkspaceInvitation { Id = leaveRequestId, WorkspaceId = workspaceId, Status = "LEAVE_REQUESTED" };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(adminMember);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = adminRoleId, Name = "Admin" });
        _workspaceInvitationRepository.GetByIdAsync(leaveRequestId, Arg.Any<CancellationToken>()).Returns(leaveRequest);

        // Act
        var result = await _service.RejectLeaveRequestAsync(workspaceId, leaveRequestId, adminUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.REJECTED.ToString(), leaveRequest.Status);
    }
}

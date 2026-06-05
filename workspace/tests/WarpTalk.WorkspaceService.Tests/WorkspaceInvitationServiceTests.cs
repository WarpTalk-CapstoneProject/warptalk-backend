using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceInvitationServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceInvitationRepository _workspaceInvitationRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly WorkspaceInvitationService _workspaceInvitationService;

    public WorkspaceInvitationServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceInvitationRepository = Substitute.For<IWorkspaceInvitationRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceInvitationRepository.Returns(_workspaceInvitationRepository);

        _workspaceInvitationService = new WorkspaceInvitationService(_unitOfWork, Substitute.For<ILogger<WorkspaceInvitationService>>(), _authIdentity);
    }

    private void StubRoleName(Guid roleId, string roleName)
    {
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = roleName });
    }

    private void StubRoleId(string roleName, Guid roleId)
    {
        _authIdentity.GetRoleByNameAsync(roleName, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = roleName });
    }

    private void StubUserEmail(string email, Guid userId)
    {
        _authIdentity.GetUserByEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = email, FullName = "Test User" });
    }

    #region InviteMemberAsync Tests

    [Fact]
    public async Task InviteMemberAsync_ShouldSucceed_WhenWorkspaceIsBusiness()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", AllowExternalCollaboration = true };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);
        StubUserEmail("invitee@warptalk.vn", Guid.NewGuid());

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("invitee@warptalk.vn", result.Value.Invitation.Email);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldReplaceOldPendingInvitation_WhenResendingToSameEmail()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", AllowExternalCollaboration = true };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };
        
        var oldInvitation = new WorkspaceInvitation 
        { 
            Id = Guid.NewGuid(), 
            WorkspaceId = workspaceId, 
            Email = "invitee@warptalk.vn", 
            Status = InvitationStatus.PENDING.ToString(), 
            ExpiresAt = DateTime.UtcNow.AddDays(1) 
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _workspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, "invitee@warptalk.vn", Arg.Any<CancellationToken>()).Returns(oldInvitation);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);
        StubUserEmail("invitee@warptalk.vn", Guid.NewGuid());

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.REPLACED.ToString(), oldInvitation.Status);
        _workspaceInvitationRepository.Received(1).Update(oldInvitation);
        await _workspaceInvitationRepository.Received(1).AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldFail_WhenExternalInvitationNotAllowed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = false,
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"],\"AllowExternalCollaboration\":false}"
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");

        var request = new InviteMemberRequest("external@gmail.com", "Member");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldSucceed_AndDowngradeRoleToMember_WhenExternalInvitationAllowed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"],\"AllowExternalCollaboration\":true}"
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };
        var memberRoleId = Guid.NewGuid();

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", memberRoleId);

        // Admin tries to invite as Admin
        var request = new InviteMemberRequest("external@gmail.com", "Admin");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Member", result.Value.Invitation.RoleName); // Downgraded
    }

    #endregion

    #region AcceptInvitationAsync Tests

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenTokenNotFound()
    {
        // Arrange
        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((WorkspaceInvitation?)null);

        var request = new AcceptInvitationRequest("invalid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenEmailDoesNotMatch()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RoleId = roleId,
            Email = "invitee@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(5)
        };

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "different_user@warptalk.vn");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenInternalMemberAlreadyBelongsToAnotherEnterpriseWorkspace()
    {
        // Arrange
        var invitationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var userEmail = "employee@enterprise.com";

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"]}"
        };

        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = userEmail,
            RoleId = Guid.NewGuid(),
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Mock that they already belong to another Enterprise workspace as an internal member
        var otherEnterpriseWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"]}"
        };
        var memberships = new List<WorkspaceMember>
        {
            new WorkspaceMember { UserId = userId, Workspace = otherEnterpriseWorkspace, MembershipType = "Internal" }
        };

        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Is("Workspace"),
            Arg.Any<CancellationToken>())
            .Returns(memberships);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId, userEmail);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldSucceed_WhenExternalMemberJoinsMultipleEnterpriseWorkspaces()
    {
        // Arrange
        var invitationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var userEmail = "external@gmail.com";

        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"],\"AllowExternalCollaboration\":true}"
        };

        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = userEmail,
            RoleId = Guid.NewGuid(),
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(new AcceptInvitationRequest("valid_token"), userId, userEmail);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldSucceed_WhenValidPendingAndEmailMatches()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RoleId = roleId,
            Email = "invitee@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(5),
            MembershipType = "Internal"
        };

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(new Workspace { Id = workspaceId, Settings = "{}" });
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns((WorkspaceMember?)null);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        _workspaceInvitationRepository.Received(1).Update(invitation);
        await _workspaceMemberRepository.Received(1).AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewInvitationAsync_ShouldReturnMaskedEmailAndSafeInfo_WhenTokenValid()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "DeepMind WS" };
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Workspace = workspace,
            RoleId = roleId,
            Email = "deepmind@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(2)
        };

        StubRoleName(roleId, "Admin");

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);

        // Act
        var result = await _workspaceInvitationService.PreviewInvitationAsync("some_token");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("DeepMind WS", result.Value.WorkspaceName);
        Assert.Equal("Admin", result.Value.RoleName);
        Assert.Equal("de***@warptalk.vn", result.Value.MaskedEmail);
        Assert.Equal("PENDING", result.Value.Status);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldSucceed_WhenInternalUserJoinsAnotherWorkspaceAsExternalPartner()
    {
        // Arrange
        var invitationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var userEmail = "employee@company-a.com";
        var token = "some_token";
        var tokenHash = TokenHasher.Hash(token);

        var request = new AcceptInvitationRequest(token);
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = userEmail,
            Status = "PENDING",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            MembershipType = "External"
        };

        var workspaceB = new Workspace { Id = workspaceId, Settings = "{\"VerifiedDomains\":[]}" };

        _workspaceInvitationRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspaceB);

        var workspaceA = new Workspace { Id = Guid.NewGuid(), Settings = "{\"VerifiedDomains\":[\"company-a.com\"]}" };
        var existingMembership = new WorkspaceMember { UserId = userId, Workspace = workspaceA, MembershipType = "Internal" };
        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Workspace", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { existingMembership });

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId, userEmail);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenUserTriesToJoinAnotherWorkspaceAsInternal()
    {
        // Arrange
        var invitationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var userEmail = "employee@company.com";
        var token = "some_token";
        var tokenHash = TokenHasher.Hash(token);

        var request = new AcceptInvitationRequest(token);
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = userEmail,
            Status = "PENDING",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            MembershipType = "Internal"
        };

        var workspaceB = new Workspace { Id = workspaceId, Settings = "{\"VerifiedDomains\":[\"company.com\"]}" };

        _workspaceInvitationRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspaceB);

        var workspaceA = new Workspace { Id = Guid.NewGuid(), Settings = "{\"VerifiedDomains\":[\"company.com\"]}" };
        var existingMembership = new WorkspaceMember { UserId = userId, Workspace = workspaceA, MembershipType = "Internal" };
        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Workspace", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { existingMembership });

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId, userEmail);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    #endregion
}

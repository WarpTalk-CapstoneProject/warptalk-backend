using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceMemberServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly WorkspaceMemberService _workspaceMemberService;

    public WorkspaceMemberServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);

        _workspaceMemberService = new WorkspaceMemberService(_unitOfWork, Substitute.For<ILogger<WorkspaceMemberService>>(), _authIdentity);
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

    #region ListMembersAsync Tests

    [Fact]
    public async Task ListMembersAsync_ShouldSucceed_WhenRequesterIsMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10, Search: "John");

        // Mock that requester is member
        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace 
            { 
                Id = workspaceId, 
                Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}"
            });

        // Requester check for external caller
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "Internal" });

        var memberUserId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var members = new List<WorkspaceMember>
        {
            new() 
            { 
                Id = Guid.NewGuid(), 
                WorkspaceId = workspaceId, 
                UserId = memberUserId, 
                RoleId = memberRoleId,
                Status = "Active", 
                JoinedAt = DateTime.UtcNow,
                MembershipType = "Internal"
            }
        };

        _workspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(members);

        _authIdentity.GetUserByIdAsync(memberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = memberUserId, FullName = "John Doe", Email = "john@warptalk.vn" });

        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Member" });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.Total);
        Assert.Single(result.Value.Items);
        Assert.Equal("John Doe", result.Value.Items[0].FullName);
    }

    [Fact]
    public async Task ListMembersAsync_ShouldFilterDirectory_WhenRequesterIsExternalMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"]}"
        };

        // Mock requester is member
        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        // Requester check for external caller (it is external, e.g. Gmail)
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "External" });

        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var members = new List<WorkspaceMember>
        {
            new() 
            { 
                Id = Guid.NewGuid(), 
                WorkspaceId = workspaceId, 
                UserId = adminUserId, 
                RoleId = adminRoleId,
                Status = "Active", 
                JoinedAt = DateTime.UtcNow,
                MembershipType = "Internal"
            }
        };

        _workspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(members);

        _authIdentity.GetUserByIdAsync(adminUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = adminUserId, FullName = "Admin User", Email = "admin@enterprise.com" });

        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminRoleId, Name = "Admin" });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value.Items);
        Assert.Equal("Admin User", result.Value.Items[0].FullName);
    }

    [Fact]
    public async Task ListMembersAsync_ShouldFail_WhenRequesterIsNotMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        // Mock that requester is NOT member
        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    #endregion

    #region RemoveMemberAsync Tests

    [Fact]
    public async Task RemoveMemberAsync_ShouldSucceed_WhenOwnerRemovesMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        
        var workspace = new Workspace { Id = workspaceId };
        var ownerRoleId = Guid.NewGuid();
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        var targetRoleId = Guid.NewGuid();
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = targetRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        // Mock exec user (owner)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Mock target member
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(targetMember)),
            "", Arg.Any<CancellationToken>()).Returns(targetMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(targetRoleId, "Member");

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, targetUserId, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(targetMember.RemovedAt);
        Assert.Equal("Removed", targetMember.Status);
        Assert.Equal(ownerUserId, targetMember.RemovedBy);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldFail_WhenAdminTriesToRemoveOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        
        var workspace = new Workspace { Id = workspaceId };
        var adminRoleId = Guid.NewGuid();
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var ownerRoleId = Guid.NewGuid();
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(adminRoleId, "Admin");
        StubRoleName(ownerRoleId, "Owner");

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, ownerUserId, adminUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldFail_WhenLastOwnerTriesToLeave()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        
        var workspace = new Workspace { Id = workspaceId };
        var ownerRoleId = Guid.NewGuid();
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleId("Owner", ownerRoleId);

        // Mock that there's only 1 active owner
        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, ownerUserId, ownerUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldSucceed_WhenOwnerLeavesAndAnotherOwnerExists()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        
        var workspace = new Workspace { Id = workspaceId };
        var ownerRoleId = Guid.NewGuid();
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleId("Owner", ownerRoleId);

        // Mock that there are 2 active owners
        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(2);

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, ownerUserId, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(ownerMember.RemovedAt);
    }

    #endregion

    #region ChangeMemberRoleAsync Tests

    [Fact]
    public async Task ChangeMemberRoleAsync_ShouldSucceed_WhenOwnerPromotesMemberToAdmin()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var targetRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = targetRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(targetMember)),
            "", Arg.Any<CancellationToken>()).Returns(targetMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(targetRoleId, "Member");
        StubRoleId("Admin", adminRoleId);

        // Act
        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(adminRoleId, targetMember.RoleId);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeMemberRoleAsync_ShouldFail_WhenAdminTriesToDemoteOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(adminRoleId, "Admin");
        StubRoleName(ownerRoleId, "Owner");

        // Act
        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, ownerUserId, "Member", adminUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task ChangeMemberRoleAsync_ShouldFail_WhenLastOwnerTriesToDemoteSelf()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(ownerRoleId, "Owner");

        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, ownerUserId, "Admin", ownerUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    #endregion

    #region TransferOwnershipAsync Tests

    [Fact]
    public async Task TransferOwnershipAsync_ShouldFail_WhenCallerIsNotOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var newOwnerId = Guid.NewGuid();
        var nonOwnerId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, OwnerId = Guid.NewGuid() }; // owner is someone else

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Act
        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, newOwnerId, nonOwnerId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipAsync_ShouldFail_WhenNewOwnerNotMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var newOwnerId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, OwnerId = ownerId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, newOwnerId, ownerId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipAsync_ShouldFail_WhenNewOwnerIsExternal()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var newOwnerId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            OwnerId = ownerId,
            Settings = "{\"VerifiedDomains\":[\"company.com\"]}"
        };
        var newOwnerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = newOwnerId, MembershipType = "External" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(newOwnerMember);

        // Act
        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, newOwnerId, ownerId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipAsync_ShouldSucceed_WhenValidOwnerTransfer()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var newOwnerId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var workspace = new Workspace
        {
            Id = workspaceId,
            OwnerId = ownerId,
            Settings = "{\"VerifiedDomains\":[\"company.com\"]}"
        };

        var currentOwnerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerId, RoleId = ownerRoleId };
        var newOwnerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = newOwnerId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        // Mock finding new owner member
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(newOwnerMember)),
            "", Arg.Any<CancellationToken>()).Returns(newOwnerMember);

        // Mock finding current owner member
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(currentOwnerMember)),
            "", Arg.Any<CancellationToken>()).Returns(currentOwnerMember);

        StubRoleId("Owner", ownerRoleId);
        StubRoleId("Admin", adminRoleId);

        // Act
        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, newOwnerId, ownerId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(newOwnerId, workspace.OwnerId);
        Assert.Equal(adminRoleId, currentOwnerMember.RoleId);
        Assert.Equal(ownerRoleId, newOwnerMember.RoleId);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    #endregion
}

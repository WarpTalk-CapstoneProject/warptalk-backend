using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Caching;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class WorkspaceMemberServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly IUserRepository _userRepository;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly WorkspaceService _workspaceService;

    public WorkspaceMemberServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _roleRepository = Substitute.For<IRoleRepository>();
        _userRepository = Substitute.For<IUserRepository>();
        _workspaceCache = Substitute.For<IWorkspaceCacheService>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.RoleRepository.Returns(_roleRepository);
        _unitOfWork.UserRepository.Returns(_userRepository);

        _workspaceService = new WorkspaceService(_unitOfWork, _workspaceCache, Substitute.For<ILogger<WorkspaceService>>());
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

        var requesterUser = new User { Id = requesterUserId, Email = "requester@warptalk.vn" };
        _userRepository.GetByIdAsync(requesterUserId, Arg.Any<CancellationToken>()).Returns(requesterUser);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace 
            { 
                Id = workspaceId, 
                Type = "enterprise",
                Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}"
            });

        var members = new List<WorkspaceMember>
        {
            new() 
            { 
                Id = Guid.NewGuid(), 
                WorkspaceId = workspaceId, 
                UserId = Guid.NewGuid(), 
                Status = "Active", 
                JoinedAt = DateTime.UtcNow,
                User = new User { FullName = "John Doe", Email = "john@warptalk.vn" },
                Role = new Role { Name = "Member" }
            }
        };

        _workspaceMemberRepository.GetMembersByWorkspaceAsync(workspaceId, query.Page, query.PageSize, query.Search, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((members, 1));

        // Act
        var result = await _workspaceService.ListMembersAsync(workspaceId, query, requesterUserId);

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

        var requesterUser = new User { Id = requesterUserId, Email = "external@gmail.com" };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Type = "enterprise",
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"]}"
        };

        // Mock requester is member
        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _userRepository.GetByIdAsync(requesterUserId, Arg.Any<CancellationToken>())
            .Returns(requesterUser);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var adminUser = new User { FullName = "Admin User", Email = "admin@enterprise.com" };
        var members = new List<WorkspaceMember>
        {
            new() 
            { 
                Id = Guid.NewGuid(), 
                WorkspaceId = workspaceId, 
                UserId = Guid.NewGuid(), 
                Status = "Active", 
                JoinedAt = DateTime.UtcNow,
                User = adminUser,
                Role = new Role { Name = "Admin" }
            }
        };

        // When listing, onlyAdminsAndOwners should be true
        _workspaceMemberRepository.GetMembersByWorkspaceAsync(workspaceId, query.Page, query.PageSize, query.Search, true, Arg.Any<CancellationToken>())
            .Returns((members, 1));

        // Act
        var result = await _workspaceService.ListMembersAsync(workspaceId, query, requesterUserId);

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
        var result = await _workspaceService.ListMembersAsync(workspaceId, query, requesterUserId);

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
        
        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, Role = new Role { Name = "Member" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        // Mock exec user (owner)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Mock target member
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(targetMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(targetMember);

        // Act
        var result = await _workspaceService.RemoveMemberAsync(workspaceId, targetUserId, ownerUserId);

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
        
        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, Role = new Role { Name = "Admin" } };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(adminMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Act
        var result = await _workspaceService.RemoveMemberAsync(workspaceId, ownerUserId, adminUserId);

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
        
        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Mock that there's only 1 active owner
        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        var result = await _workspaceService.RemoveMemberAsync(workspaceId, ownerUserId, ownerUserId);

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
        
        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Mock that there are 2 active owners
        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        // Act
        var result = await _workspaceService.RemoveMemberAsync(workspaceId, ownerUserId, ownerUserId);

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
        var adminRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, Role = new Role { Name = "Member" } };
        var adminRole = new Role { Id = adminRoleId, Name = "Admin" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(targetMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(targetMember);

        _roleRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<Role, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminRole);

        // Act
        var result = await _workspaceService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", ownerUserId);

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

        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, Role = new Role { Name = "Admin" } };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(adminMember);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Act
        var result = await _workspaceService.ChangeMemberRoleAsync(workspaceId, ownerUserId, "Member", adminUserId);

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

        var workspace = new Workspace { Id = workspaceId, Type = "business" };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "Role", Arg.Any<CancellationToken>()).Returns(ownerMember);

        _workspaceMemberRepository.CountActiveOwnersAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        var result = await _workspaceService.ChangeMemberRoleAsync(workspaceId, ownerUserId, "Admin", ownerUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    #endregion
}

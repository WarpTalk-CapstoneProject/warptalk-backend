using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using AppWorkspaceService = WarpTalk.WorkspaceService.Application.Services.WorkspaceService;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly AppWorkspaceService _workspaceService;

    public WorkspaceServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _workspaceCache = Substitute.For<IWorkspaceCacheService>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);

        _workspaceService = new AppWorkspaceService(_unitOfWork, _workspaceCache, Substitute.For<ILogger<AppWorkspaceService>>(), _authIdentity);
    }

    private void StubUser(Guid userId, User user)
    {
        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
    }

    private void StubRoleByName(string roleName, Role role)
    {
        _authIdentity.GetRoleByNameAsync(roleName, Arg.Any<CancellationToken>()).Returns(role);
    }

    #region CreateWorkspaceAsync Tests

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_AndBootstrapOwner_WhenPayloadValid()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@warptalk.vn" };
        var request = new CreateWorkspaceRequest("DeepMind Team", "AI Research", "https://cdn.com/logo.png");

        StubUser(userId, user);
        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);
        _workspaceRepository.AnyAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("DeepMind Team", result.Value.Name);
        Assert.Equal("deepmind-team", result.Value.Slug);
        Assert.Equal("Owner", result.Value.Role);

        // Verify transaction commits
        await _workspaceRepository.Received(1).AddAsync(Arg.Is<Workspace>(w => w.Name == "DeepMind Team" && w.OwnerId == userId), Arg.Any<CancellationToken>());
        await _workspaceMemberRepository.Received(1).AddAsync(Arg.Is<WorkspaceMember>(m => m.UserId == userId && m.RoleId == ownerRole.Id), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenNameIsEmpty()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreateWorkspaceRequest("", "Description", null);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenUserIsAlreadyInternalMemberOfAnotherEnterpriseWorkspace()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "employee@enterprise.com" };
        var request = new CreateWorkspaceRequest("New Enterprise", "Enterprise WS", null);

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        // Mock that they already belong to another Enterprise workspace as an internal member
        var otherEnterpriseWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"]}"
        };
        var memberships = new List<WorkspaceMember>
        {
            new WorkspaceMember { UserId = userId, Workspace = otherEnterpriseWorkspace }
        };

        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Is("Workspace"),
            Arg.Any<CancellationToken>())
            .Returns(memberships);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("already an internal member", result.Error);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_AndInitializeVerifiedDomains_WhenCreatingEnterpriseWorkspace()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@enterprise.com" };
        var request = new CreateWorkspaceRequest("New Enterprise", "Enterprise WS", null);

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Is("Workspace"),
            Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());

        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        
        // Verify we saved the workspace with VerifiedDomains set to enterprise.com
        await _workspaceRepository.Received(1).AddAsync(Arg.Is<Workspace>(w => 
            w.Settings.Contains("enterprise.com")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenDomainRegisteredElsewhere()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "user@company.com" };
        var request = new CreateWorkspaceRequest("New Work", "Desc", null);

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _workspaceMemberRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Workspace", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());

        // Mock another active workspace verifying "company.com"
        var otherWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            Settings = "{\"VerifiedDomains\":[\"company.com\"]}"
        };
        _workspaceRepository.FindAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<Workspace> { otherWorkspace });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("corporate domain registered with another workspace", result.Error);
    }

    #endregion

    #region GetWorkspacesAsync Tests

    [Fact]
    public async Task GetWorkspacesAsync_ShouldReturnPaginatedList_WithTotalCount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 2, PageSize: 5, Search: null);

        var workspaces = new List<Workspace>
        {
            new() { Id = Guid.NewGuid(), Name = "Workspace 1", Slug = "ws-1" },
            new() { Id = Guid.NewGuid(), Name = "Workspace 2", Slug = "ws-2" }
        };

        _workspaceRepository.GetWorkspacesForUserAsync(userId, query.Page, query.PageSize, query.Search, Arg.Any<CancellationToken>())
            .Returns((workspaces, 12));

        // Act
        var result = await _workspaceService.GetWorkspacesAsync(query, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Page);
        Assert.Equal(5, result.Value.PageSize);
        Assert.Equal(12, result.Value.Total);
        Assert.Equal(2, result.Value.Items.Count);
    }

    #endregion

    #region SelectWorkspaceAsync Tests

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldSaveInCache_WhenUserIsMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace 
            { 
                Id = workspaceId, 
                Name = "DeepMind", 
                Slug = "deepmind", 
                Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}" 
            });

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "test@warptalk.vn" });

        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(workspaceId, result.Value.SelectedWorkspaceId);

        // Verify cache service received the update
        await _workspaceCache.Received(1).SetActiveWorkspaceDetailsAsync(userId, workspaceId, "Member", "Internal", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember)null);

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    #endregion

    #region GetWorkspaceByIdAsync Tests

    [Fact]
    public async Task GetWorkspaceByIdAsync_ShouldSucceed_WhenUserIsMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "DeepMind",
            Slug = "deepmind",
            LogoUrl = "logo.png",
            CreatedAt = DateTime.UtcNow
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Owner" });

        // Act
        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("DeepMind", result.Value.Name);
        Assert.Equal("Owner", result.Value.Role);
    }

    [Fact]
    public async Task GetWorkspaceByIdAsync_ShouldFail_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember)null);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task GetWorkspaceByIdAsync_ShouldFail_WhenWorkspaceNotFound()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((Workspace)null);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    #endregion

    #region Workspace Settings Tests

    [Fact]
    public async Task GetWorkspaceSettingsAsync_ShouldReturnParsedSettings_WhenUserIsMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId };
        
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"DefaultLanguage\":\"vi\",\"Timezone\":\"Asia/Ho_Chi_Minh\",\"VoiceCloningEnabled\":false}"
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        _workspaceRepository.GetSettingsAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceConfiguration 
            { 
                DefaultLanguage = "vi", 
                Timezone = "Asia/Ho_Chi_Minh", 
                VoiceCloningEnabled = false 
            });

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "test@warptalk.vn" });

        // Act
        var result = await _workspaceService.GetWorkspaceSettingsAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("vi", result.Value.DefaultLanguage);
        Assert.Equal("Asia/Ho_Chi_Minh", result.Value.Timezone);
        Assert.False(result.Value.VoiceCloningEnabled);
    }

    [Fact]
    public async Task GetWorkspaceSettingsAsync_ShouldReturnDefaultSettings_WhenSettingsColumnIsEmpty()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId };
        
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{}"
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        _workspaceRepository.GetSettingsAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceConfiguration());

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "test@warptalk.vn" });

        // Act
        var result = await _workspaceService.GetWorkspaceSettingsAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("en", result.Value.DefaultLanguage);
        Assert.True(result.Value.VoiceCloningEnabled);
    }

    [Fact]
    public async Task GetWorkspaceSettingsAsync_ShouldFail_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember)null);

        // Act
        var result = await _workspaceService.GetWorkspaceSettingsAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldSucceed_WhenUserIsOwnerOrAdmin()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceSettingsDto(
            "vi",
            "Asia/Ho_Chi_Minh",
            new List<string>(),
            false,
            5,
            30,
            true,
            new List<string>(),
            true
        );

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId });
        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "admin@warptalk.vn" });

        var memberRoleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Admin" });

        _workspaceRepository.UpdateSettingsAsync(workspaceId, Arg.Any<WorkspaceConfiguration>(), userId, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.True(result.IsSuccess);
        await _workspaceRepository.Received(1).UpdateSettingsAsync(workspaceId, Arg.Is<WorkspaceConfiguration>(c => c.DefaultLanguage == "vi"), userId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenUserIsNotOwnerOrAdmin()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceSettingsDto(
            "vi",
            "Asia/Ho_Chi_Minh",
            new List<string>(),
            false,
            5,
            30,
            true,
            new List<string>(),
            true
        );

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId });
        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "user@warptalk.vn" });

        var memberRoleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Member" });

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion
}

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
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Application.Interfaces.Caching;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class WorkspaceServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IUserRepository _userRepository;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly WorkspaceService _workspaceService;

    public WorkspaceServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _userRepository = Substitute.For<IUserRepository>();
        _workspaceCache = Substitute.For<IWorkspaceCacheService>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.UserRepository.Returns(_userRepository);

        _workspaceService = new WorkspaceService(_unitOfWork, _workspaceCache, Substitute.For<ILogger<WorkspaceService>>());
    }

    #region CreateWorkspaceAsync Tests

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_AndBootstrapOwner_WhenPayloadValid()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@warptalk.vn" };
        var request = new CreateWorkspaceRequest("DeepMind Team", "AI Research", "https://cdn.com/logo.png");

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
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
        await _workspaceMemberRepository.Received(1).AddAsync(Arg.Is<WorkspaceMember>(m => m.UserId == userId && m.Role.Name == "Owner"), Arg.Any<CancellationToken>());
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

        // Assume the repository mock returns these items through an async query method or IQueryable
        // We will implement GetWorkspacesPagedAsync on our Repository or handle it in the Service.
        // For the unit test, we'll setup the service mapping.
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
        
        // Mock that member exists
        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, Name = "DeepMind", Slug = "deepmind" });

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(workspaceId, result.Value.SelectedWorkspaceId);

        // Verify cache service received the update
        await _workspaceCache.Received(1).SetActiveWorkspaceAsync(userId, workspaceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

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
        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = ownerRole.Id,
            Role = ownerRole
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "DeepMind",
            Slug = "deepmind",
            LogoUrl = "logo.png",
            CreatedAt = DateTime.UtcNow
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Role", Arg.Any<CancellationToken>())
            .Returns(member);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

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

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Role", Arg.Any<CancellationToken>())
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

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Role", Arg.Any<CancellationToken>())
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

    #region Invite & Transfer Validation Tests

    [Fact]
    public async Task InviteMemberAsync_ShouldFail_WhenWorkspaceIsPersonal()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "My Workspace", Slug = "my-workspace", Type = "personal" };
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member");

        // Act
        var result = await _workspaceService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Contains("not allowed in a Personal Workspace", result.Error);
    }

    [Fact]
    public async Task TransferOwnershipAsync_ShouldFail_WhenWorkspaceIsPersonal()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "My Workspace", Slug = "my-workspace", Type = "personal" };
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Act
        var result = await _workspaceService.TransferOwnershipAsync(workspaceId, Guid.NewGuid());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Contains("not allowed in a Personal Workspace", result.Error);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldSucceed_WhenWorkspaceIsBusiness()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", Type = "business" };
        var role = new Role { Id = roleId, Name = "Member" };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, Role = new Role { Name = "Owner" } };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<System.Func<WorkspaceMember, bool>>>(), "Role", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _unitOfWork.RoleRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<System.Func<Role, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(role);

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member");

        // Act
        var result = await _workspaceService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("invitee@warptalk.vn", result.Value.Invitation.Email);
    }

    [Fact]
    public async Task TransferOwnershipAsync_ShouldSucceed_WhenWorkspaceIsBusiness()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", Type = "business" };
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        // Act
        var result = await _workspaceService.TransferOwnershipAsync(workspaceId, Guid.NewGuid());

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldReplaceOldPendingInvitation_WhenResendingToSameEmail()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", Type = "business" };
        var role = new Role { Id = roleId, Name = "Member" };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, Role = new Role { Name = "Owner" } };
        
        var invitationRepo = Substitute.For<IWorkspaceInvitationRepository>();
        var oldInvitation = new WorkspaceInvitation 
        { 
            Id = Guid.NewGuid(), 
            WorkspaceId = workspaceId, 
            Email = "invitee@warptalk.vn", 
            Status = InvitationStatus.PENDING.ToString(), 
            ExpiresAt = DateTime.UtcNow.AddDays(1) 
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<System.Func<WorkspaceMember, bool>>>(), "Role", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _unitOfWork.RoleRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<System.Func<Role, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(role);
        _unitOfWork.WorkspaceInvitationRepository.Returns(invitationRepo);

        invitationRepo.GetPendingByEmailAsync(workspaceId, "invitee@warptalk.vn", Arg.Any<CancellationToken>()).Returns(oldInvitation);

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member");

        // Act
        var result = await _workspaceService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.REPLACED.ToString(), oldInvitation.Status);
        invitationRepo.Received(1).Update(oldInvitation);
        await invitationRepo.Received(1).AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenTokenNotFound()
    {
        // Arrange
        var invitationRepo = Substitute.For<IWorkspaceInvitationRepository>();
        _unitOfWork.WorkspaceInvitationRepository.Returns(invitationRepo);
        invitationRepo.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((WorkspaceInvitation)null);

        var request = new AcceptInvitationRequest("invalid_token");

        // Act
        var result = await _workspaceService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

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

        var invitationRepo = Substitute.For<IWorkspaceInvitationRepository>();
        _unitOfWork.WorkspaceInvitationRepository.Returns(invitationRepo);
        invitationRepo.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceService.AcceptInvitationAsync(request, Guid.NewGuid(), "different_user@warptalk.vn");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldSucceed_WhenValidPendingAndEmailMatches()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var role = new Role { Id = roleId, Name = "Member" };
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RoleId = roleId,
            Role = role,
            Email = "invitee@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(5)
        };

        var invitationRepo = Substitute.For<IWorkspaceInvitationRepository>();
        _unitOfWork.WorkspaceInvitationRepository.Returns(invitationRepo);
        invitationRepo.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);
        
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<System.Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns((WorkspaceMember)null);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        invitationRepo.Received(1).Update(invitation);
        await _workspaceMemberRepository.Received(1).AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewInvitationAsync_ShouldReturnMaskedEmailAndSafeInfo_WhenTokenValid()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "DeepMind WS" };
        var role = new Role { Id = roleId, Name = "Admin" };
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Workspace = workspace,
            RoleId = roleId,
            Role = role,
            Email = "deepmind@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(2)
        };

        var invitationRepo = Substitute.For<IWorkspaceInvitationRepository>();
        _unitOfWork.WorkspaceInvitationRepository.Returns(invitationRepo);
        invitationRepo.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);

        // Act
        var result = await _workspaceService.PreviewInvitationAsync("some_token");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("DeepMind WS", result.Value.WorkspaceName);
        Assert.Equal("Admin", result.Value.RoleName);
        Assert.Equal("de***@warptalk.vn", result.Value.MaskedEmail);
        Assert.Equal("PENDING", result.Value.Status);
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
        var expectedSettings = new WorkspaceConfiguration
        {
            DefaultLanguage = "vi",
            Timezone = "Asia/Ho_Chi_Minh",
            VoiceCloningEnabled = false
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(member);
        _workspaceRepository.GetSettingsAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(expectedSettings);

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
        var expectedSettings = new WorkspaceConfiguration();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(member);
        _workspaceRepository.GetSettingsAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(expectedSettings);

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

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
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
        var newSettings = new WorkspaceConfiguration
        {
            DefaultLanguage = "vi",
            Timezone = "Asia/Ho_Chi_Minh",
            VoiceCloningEnabled = false
        };

        _workspaceMemberRepository.IsOwnerOrAdminAsync(workspaceId, userId, Arg.Any<CancellationToken>())
            .Returns(true);
        _workspaceRepository.UpdateSettingsAsync(workspaceId, newSettings, userId, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.True(result.IsSuccess);
        await _workspaceRepository.Received(1).UpdateSettingsAsync(workspaceId, newSettings, userId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenUserIsNotOwnerOrAdmin()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceConfiguration { DefaultLanguage = "vi" };

        _workspaceMemberRepository.IsOwnerOrAdminAsync(workspaceId, userId, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion
}

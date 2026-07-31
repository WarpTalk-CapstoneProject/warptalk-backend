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
using WarpTalk.WorkspaceService.Domain.Constants;
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

        _workspaceMemberService = new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            Substitute.For<IWorkspaceEventPublisher>());
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

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace 
            { 
                Id = workspaceId, 
                Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}"
            });

        var requesterRoleId = Guid.NewGuid();
        // Requester check
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "Internal", RoleId = requesterRoleId });

        _authIdentity.GetRoleByIdAsync(requesterRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = requesterRoleId, Name = "Member" });

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

        _workspaceMemberRepository.GetPagedMembersAsync(workspaceId, query.Page, query.PageSize, false, true, Arg.Any<CancellationToken>())
            .Returns((members, members.Count));

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
        Assert.Equal("john@warptalk.vn", result.Value.Items[0].Email); // WT-181: internal members can see each other's email
    }

    [Fact]
    public async Task ListMembersAsync_ShouldFail_WhenRequesterIsExternalMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        // Requester check: external member
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "External" });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task ListMembersAsync_ShouldShowAllMembersIncludingRemovedAndBanned_WhenRequesterIsOwnerOrAdmin()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId });

        var requesterRoleId = Guid.NewGuid();
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "Internal", RoleId = requesterRoleId });

        _authIdentity.GetRoleByIdAsync(requesterRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = requesterRoleId, Name = "Owner" });

        var activeMemberUserId = Guid.NewGuid();
        var removedMemberUserId = Guid.NewGuid();
        var activeRoleId = Guid.NewGuid();
        var removedRoleId = Guid.NewGuid();

        var members = new List<WorkspaceMember>
        {
            new() { WorkspaceId = workspaceId, UserId = activeMemberUserId, RoleId = activeRoleId, Status = "Active", JoinedAt = DateTime.UtcNow.AddDays(-1) },
            new() { WorkspaceId = workspaceId, UserId = removedMemberUserId, RoleId = removedRoleId, Status = "Removed", JoinedAt = DateTime.UtcNow, RemovedAt = DateTime.UtcNow }
        };

        _workspaceMemberRepository.GetPagedMembersAsync(workspaceId, query.Page, query.PageSize, true, true, Arg.Any<CancellationToken>())
            .Returns((members, members.Count));

        _authIdentity.GetUserByIdAsync(activeMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = activeMemberUserId, FullName = "Active User", Email = "active@warptalk.vn" });
        _authIdentity.GetUserByIdAsync(removedMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = removedMemberUserId, FullName = "Removed User", Email = "removed@warptalk.vn" });

        _authIdentity.GetRoleByIdAsync(activeRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = activeRoleId, Name = "Member" });
        _authIdentity.GetRoleByIdAsync(removedRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = removedRoleId, Name = "Member" });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Total);
        Assert.Equal("Active User", result.Value.Items[0].FullName);
        Assert.Equal("active@warptalk.vn", result.Value.Items[0].Email); // Owner/Admin can see emails
        Assert.Equal("Removed User", result.Value.Items[1].FullName);
        Assert.Equal("removed@warptalk.vn", result.Value.Items[1].Email);
    }

    [Fact]
    public async Task ListMembersAsync_ShouldFail_WhenRequesterIsNotMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        // Mock that requester is NOT member
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

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
    public async Task ChangeMemberRoleAsync_ShouldFail_WhenAdminTriesToPromoteMember()
    {
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId };
        var admin = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(admin)), "", Arg.Any<CancellationToken>()).Returns(admin);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(target)), "", Arg.Any<CancellationToken>()).Returns(target);
        StubRoleName(adminRoleId, "Admin");
        StubRoleName(memberRoleId, "Member");

        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", adminUserId);

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

    [Fact]
    public async Task PreviewAndApplyRoleChange_ShouldUpdateExistingMembershipRole_WithoutChangingMeetingCapability()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId };
        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = adminRoleId, MembershipType = "Internal", CanCreateMeetings = true };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(owner)), "", Arg.Any<CancellationToken>()).Returns(owner);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(target)), "", Arg.Any<CancellationToken>()).Returns(target);
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = adminRoleId, Name = "Admin" });
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>()).Returns(new Role { Id = memberRoleId, Name = "Member" });
        _authIdentity.GetUserByIdAsync(targetUserId, Arg.Any<CancellationToken>()).Returns(new User { Id = targetUserId, FullName = "Target User", Email = "target@example.com" });

        var preview = await _workspaceMemberService.PreviewMemberRoleChangeAsync(workspaceId, targetUserId, "Member", ownerUserId);
        Assert.True(preview.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(preview.Value?.PreviewToken));

        var apply = await _workspaceMemberService.ApplyMemberRoleChangeAsync(
            workspaceId,
            targetUserId,
            new ApplyWorkspaceRoleChangeRequest("Member", Guid.NewGuid().ToString("N"), preview.Value!.PreviewToken!),
            ownerUserId);

        Assert.True(apply.IsSuccess);
        Assert.Equal(memberRoleId, target.RoleId);
        Assert.True(target.CanCreateMeetings);
        Assert.Equal("Admin", apply.Value!.OldRole);
        Assert.Equal("Member", apply.Value.NewRole);
        Assert.NotEqual(Guid.Empty, apply.Value.AuditId);
    }

    [Fact]
    public async Task ApplyMemberRoleChange_ShouldRejectStalePreview_WhenRoleChangedAfterPreview()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId };
        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = adminRoleId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(owner)), "", Arg.Any<CancellationToken>()).Returns(owner);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(target)), "", Arg.Any<CancellationToken>()).Returns(target);
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = adminRoleId, Name = "Admin" });
        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>()).Returns(new Role { Id = memberRoleId, Name = "Member" });
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>()).Returns(new Role { Id = memberRoleId, Name = "Member" });

        var preview = await _workspaceMemberService.PreviewMemberRoleChangeAsync(workspaceId, targetUserId, "Member", ownerUserId);
        Assert.True(preview.IsSuccess);

        target.RoleId = memberRoleId;
        var apply = await _workspaceMemberService.ApplyMemberRoleChangeAsync(
            workspaceId,
            targetUserId,
            new ApplyWorkspaceRoleChangeRequest("Member", Guid.NewGuid().ToString("N"), preview.Value!.PreviewToken!),
            ownerUserId);

        Assert.False(apply.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, apply.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.RoleChangeStale, apply.Error);
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

    #region UpdateMemberAsync Tests

    [Fact]
    public async Task UpdateMemberAsync_ShouldSucceed_WhenOwnerUpdatesAdmin()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var targetRoleId = Guid.NewGuid();

        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = targetRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        // Mock executing member (owner)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        // Mock target member (admin)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(targetMember)),
            "", Arg.Any<CancellationToken>()).Returns(targetMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(targetRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, targetUserId, request, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(targetMember.CanCreateMeetings);
        _workspaceMemberRepository.Received(1).Update(targetMember);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldSucceed_WhenAdminUpdatesSelf()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        // Mock executing member & target member (same admin member)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        StubRoleName(adminRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, adminUserId, request, adminUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(adminMember.CanCreateMeetings);
        _workspaceMemberRepository.Received(1).Update(adminMember);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldFail_WhenAdminUpdatesPeerAdmin()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminAUserId = Guid.NewGuid();
        var adminBUserId = Guid.NewGuid();
        var adminARoleId = Guid.NewGuid();
        var adminBRoleId = Guid.NewGuid();

        var adminAMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminAUserId, RoleId = adminARoleId };
        var adminBMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminBUserId, RoleId = adminBRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        // Mock executing member (Admin A)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminAMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminAMember);

        // Mock target member (Admin B)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminBMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminBMember);

        StubRoleName(adminARoleId, "Admin");
        StubRoleName(adminBRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, adminBUserId, request, adminAUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.AdminCannotModifyPeerAdmin, result.Error);
        Assert.False(adminBMember.CanCreateMeetings);
        _workspaceMemberRepository.DidNotReceive().Update(Arg.Any<WorkspaceMember>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldFail_WhenAdminUpdatesOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        // Mock executing member (admin)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        // Mock target member (owner)
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(adminRoleId, "Admin");
        StubRoleName(ownerRoleId, "Owner");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, ownerUserId, request, adminUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal("Admins cannot modify settings of workspace owners.", result.Error);
        Assert.False(ownerMember.CanCreateMeetings);
        _workspaceMemberRepository.DidNotReceive().Update(Arg.Any<WorkspaceMember>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    #endregion
}

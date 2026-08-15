using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Extensions;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceMemberServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceEventPublisher _eventPublisher;
    private readonly WorkspaceMemberService _workspaceMemberService;

    public WorkspaceMemberServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _eventPublisher = Substitute.For<IWorkspaceEventPublisher>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);

        _workspaceMemberService = new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            _eventPublisher,
            CreatePreviewSigningConfiguration());
    }

    private static IConfiguration CreatePreviewSigningConfiguration(string key = "test-role-preview-signing-key-with-at-least-32-characters")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RolePreviewSigningKey"] = key
            })
            .Build();
    }

    private WorkspaceMemberService CreateMemberServiceWithoutPreviewSigningKey()
    {
        return new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            _eventPublisher,
            new ConfigurationBuilder().Build());
    }

    [Fact]
    public void Constructor_ShouldRejectNullConfiguration()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            _eventPublisher,
            null!));

        Assert.Equal("configuration", ex.ParamName);
    }

    [Fact]
    public void CreateMemberMappers_ShouldWriteLowercaseActiveStatus()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var owner = WorkspaceMemberMapper.CreateOwnerMember(workspaceId, userId, roleId);
        var invited = WorkspaceMemberMapper.CreateInvitationMember(workspaceId, Guid.NewGuid(), roleId, "Internal");

        Assert.Equal(WorkspaceMemberStatus.Active.ToStorageValue(), owner.Status);
        Assert.Equal(WorkspaceMemberStatus.Active.ToStorageValue(), invited.Status);
    }

    /// <summary>
    /// The column has DEFAULT true in Postgres, but nothing in this process ever saw that default:
    /// EF treated true as the property's sentinel, the mappers left the property at the CLR default
    /// false, and EF therefore wrote false explicitly on every INSERT. The result was a workspace
    /// Owner refused meeting creation in the workspace they had just created, and the same for
    /// everyone who joined by accepting an invitation.
    ///
    /// This asserts the value in the ENTITY the mappers hand to EF, which is the only place the
    /// answer is now decided — a test that asserted the column default would have passed throughout
    /// the entire lifetime of the bug.
    /// </summary>
    [Fact]
    public void CreateMemberMappers_ShouldGrantMeetingCreation()
    {
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var owner = WorkspaceMemberMapper.CreateOwnerMember(workspaceId, Guid.NewGuid(), roleId);
        var invited = WorkspaceMemberMapper.CreateInvitationMember(workspaceId, Guid.NewGuid(), roleId, "Internal");

        Assert.True(owner.CanCreateMeetings);
        Assert.True(invited.CanCreateMeetings);
    }

    /// <summary>
    /// WT-371 #2: the grant above is for INTERNAL members. An external collaborator accepting an
    /// invitation used to receive it too, so anyone invited from outside a verified domain landed in
    /// the workspace able to open meetings — the one action that spends the tenant's credits — and
    /// with the full internal UI offering it.
    ///
    /// Asserted on the entity, for the same reason the test above is: this is where the answer is
    /// decided. The casing variants are not padding — the column is written from
    /// <c>MembershipType.External.ToString()</c> in one path and echoed from a stored row in
    /// another, and a case-sensitive comparison here would restore the bug for whichever path
    /// happened to disagree.
    /// </summary>
    [Theory]
    [InlineData("External")]
    [InlineData("external")]
    [InlineData("EXTERNAL")]
    public void CreateInvitationMember_ShouldWithholdMeetingCreation_FromExternalMembers(string membershipType)
    {
        var member = WorkspaceMemberMapper.CreateInvitationMember(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), membershipType);

        Assert.False(member.CanCreateMeetings);
    }

    /// <summary>
    /// The other half of the rule, and the reason it is written as "not External" rather than
    /// "is Internal": a workspace with no verified-domain policy classifies everyone Internal, and
    /// an unrecognised or empty value must not silently strip meeting creation from ordinary
    /// members. Only an explicit External withholds it.
    /// </summary>
    [Theory]
    [InlineData("Internal")]
    [InlineData("internal")]
    [InlineData("")]
    public void CreateInvitationMember_ShouldGrantMeetingCreation_ToEveryoneElse(string membershipType)
    {
        var member = WorkspaceMemberMapper.CreateInvitationMember(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), membershipType);

        Assert.True(member.CanCreateMeetings);
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

    [Fact]
    public async Task ListMembersAsync_ShouldNotRequirePreviewSigningKey()
    {
        var previousEnvKey = Environment.GetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY");
        try
        {
            Environment.SetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY", null);
            var serviceWithoutSigningKey = CreateMemberServiceWithoutPreviewSigningKey();
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var roleId = Guid.NewGuid();
            var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, MembershipType = "Internal", RoleId = roleId };

            _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(new Workspace { Id = workspaceId });
            _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(member);
            _workspaceMemberRepository.GetPagedMembersAsync(workspaceId, 1, 10, false, true, Arg.Any<CancellationToken>())
                .Returns((new List<WorkspaceMember> { member }, 1));
            StubRoleName(roleId, "Member");
            _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, FullName = "Member", Email = "member@example.com" });

            var result = await serviceWithoutSigningKey.ListMembersAsync(workspaceId, new GetWorkspacesQuery(), userId);

            Assert.True(result.IsSuccess);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY", previousEnvKey);
        }
    }

    [Fact]
    public void Constructor_ShouldUseEnvPreviewSigningKey_WhenJwtSecretIsPlaceholder()
    {
        var previousEnvKey = Environment.GetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY");
        try
        {
            Environment.SetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY", "env-role-preview-signing-key-with-at-least-32-characters");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Secret"] = "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!"
                })
                .Build();

            var service = new WorkspaceMemberService(
                _unitOfWork,
                Substitute.For<ILogger<WorkspaceMemberService>>(),
                _authIdentity,
                Substitute.For<IWorkspaceEventPublisher>(),
                configuration);

            Assert.NotNull(service);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY", previousEnvKey);
        }
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
        var nonMatchingUserId = Guid.NewGuid();
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
            },
            new()
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = nonMatchingUserId,
                RoleId = memberRoleId,
                Status = "Active",
                JoinedAt = DateTime.UtcNow.AddMinutes(-1),
                MembershipType = "Internal"
            }
        };

        _workspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(members);

        _authIdentity.GetUserByIdAsync(memberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = memberUserId, FullName = "John Doe", Email = "john@warptalk.vn" });
        _authIdentity.GetUserByIdAsync(nonMatchingUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = nonMatchingUserId, FullName = "Jane Smith", Email = "jane@warptalk.vn" });

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

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task ListMembersAsync_ShouldShowSuspendedMembers_WhenRequesterIsOwnerOrAdmin(string requesterRoleName)
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
            .Returns(new Role { Id = requesterRoleId, Name = requesterRoleName });

        var activeMemberUserId = Guid.NewGuid();
        var suspendedMemberUserId = Guid.NewGuid();
        var activeRoleId = Guid.NewGuid();
        var suspendedRoleId = Guid.NewGuid();

        var members = new List<WorkspaceMember>
        {
            new() { WorkspaceId = workspaceId, UserId = activeMemberUserId, RoleId = activeRoleId, Status = "Active", JoinedAt = DateTime.UtcNow.AddDays(-1) },
            new() { WorkspaceId = workspaceId, UserId = suspendedMemberUserId, RoleId = suspendedRoleId, Status = "Suspended", JoinedAt = DateTime.UtcNow }
        };

        _workspaceMemberRepository.GetPagedMembersAsync(workspaceId, query.Page, query.PageSize, true, true, Arg.Any<CancellationToken>())
            .Returns((members, members.Count));

        _authIdentity.GetUserByIdAsync(activeMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = activeMemberUserId, FullName = "Active User", Email = "active@warptalk.vn" });
        _authIdentity.GetUserByIdAsync(suspendedMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = suspendedMemberUserId, FullName = "Suspended User", Email = "suspended@warptalk.vn" });

        _authIdentity.GetRoleByIdAsync(activeRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = activeRoleId, Name = "Member" });
        _authIdentity.GetRoleByIdAsync(suspendedRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = suspendedRoleId, Name = "Member" });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Total);
        Assert.Equal("Active User", result.Value.Items[0].FullName);
        Assert.Equal("active@warptalk.vn", result.Value.Items[0].Email); // Owner/Admin can see emails
        Assert.Equal("Suspended User", result.Value.Items[1].FullName);
        Assert.Equal("suspended@warptalk.vn", result.Value.Items[1].Email);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task ListMembersAsync_ShouldExcludeRemovedMembersFromSearch_WhenRequesterIsOwnerOrAdmin(string requesterRoleName)
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var requesterUserId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10, Search: "Removed");

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId });

        var requesterRoleId = Guid.NewGuid();
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = requesterUserId, MembershipType = "Internal", RoleId = requesterRoleId });

        StubRoleName(requesterRoleId, requesterRoleName);

        var removedMemberUserId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        StubRoleName(memberRoleId, "Member");
        _authIdentity.GetUserByIdAsync(removedMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = removedMemberUserId, FullName = "Removed User", Email = "removed@warptalk.vn" });

        // The repository is the real filter, so what this asserts is the predicate the service
        // hands it: run it over the row that must not survive.
        var rowsInWorkspace = new List<WorkspaceMember>
        {
            new()
            {
                WorkspaceId = workspaceId,
                UserId = removedMemberUserId,
                RoleId = memberRoleId,
                Status = WorkspaceMemberStatus.Removed.ToStorageValue(),
                MembershipType = "Internal",
                JoinedAt = DateTime.UtcNow.AddDays(-2),
                RemovedAt = DateTime.UtcNow
            }
        };

        _workspaceMemberRepository
            .FindAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<WorkspaceMember, bool>>>().Compile();
                return (IReadOnlyList<WorkspaceMember>)rowsInWorkspace.Where(predicate).ToList();
            });

        // Act
        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, requesterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.Total);
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
        Assert.Equal(WorkspaceMemberStatus.Removed.ToStorageValue(), targetMember.Status);
        Assert.Equal(ownerUserId, targetMember.RemovedBy);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldFail_WhenAdminTriesToRemovePeerAdmin()
    {
        // UpdateMemberAsync has always refused Admin-on-Admin edits; removal, the more
        // destructive operation, did not. The web client disables the button for exactly
        // this case (members/page.tsx: `isAdmin && memberRole === "admin"`).
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminAUserId = Guid.NewGuid();
        var adminBUserId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var adminARoleId = Guid.NewGuid();
        var adminBRoleId = Guid.NewGuid();
        var adminAMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminAUserId, RoleId = adminARoleId };
        var adminBMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminBUserId, RoleId = adminBRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminAMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminAMember);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminBMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminBMember);

        StubRoleName(adminARoleId, "Admin");
        StubRoleName(adminBRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, adminBUserId, adminAUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.AdminCannotRemovePeerAdmin, result.Error);
        Assert.Null(adminBMember.RemovedAt);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldSucceed_WhenOwnerRemovesAdmin()
    {
        // The Owner keeps full authority over Admins.
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(adminRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, adminUserId, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(adminMember.RemovedAt);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldSucceed_WhenAdminLeavesVoluntarily()
    {
        // The peer-Admin guard must not trap an Admin in the workspace: self-removal is
        // handled before it and stays open.
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId };
        var adminRoleId = Guid.NewGuid();
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        StubRoleName(adminRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, adminUserId, adminUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(adminMember.RemovedAt);
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
        var calls = new List<string>();

        var workspace = new Workspace { Id = workspaceId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = targetRoleId, MembershipType = "Internal" };

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
        _eventPublisher.PublishMemberRoleChangedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("event");
                return Task.CompletedTask;
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("save");
                return 1;
            });

        // Act
        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(adminRoleId, targetMember.RoleId);
        Assert.Equal(new[] { "event", "save" }, calls);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeMemberRoleAsync_ShouldNoopWithoutEvent_WhenRoleAlreadyMatches()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId };
        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = adminRoleId, MembershipType = "Internal" };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(owner)),
            "", Arg.Any<CancellationToken>()).Returns(owner);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(expr => expr.Compile()(target)),
            "", Arg.Any<CancellationToken>()).Returns(target);
        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(adminRoleId, "Admin");

        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", ownerUserId);

        Assert.True(result.IsSuccess);
        _workspaceMemberRepository.DidNotReceive().Update(target);
        await _eventPublisher.DidNotReceive().PublishMemberRoleChangedAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
    public async Task ApplyMemberRoleChange_ShouldAcceptPreviewToken_FromAnotherServiceInstanceWithSameSigningKey()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var sharedConfiguration = CreatePreviewSigningConfiguration("shared-role-preview-signing-key-with-at-least-32-characters");
        var secondServiceInstance = new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            Substitute.For<IWorkspaceEventPublisher>(),
            sharedConfiguration);

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
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>()).Returns(new Role { Id = memberRoleId, Name = "Member" });
        _authIdentity.GetUserByIdAsync(targetUserId, Arg.Any<CancellationToken>()).Returns(new User { Id = targetUserId, FullName = "Target User", Email = "target@example.com" });

        var firstServiceInstance = new WorkspaceMemberService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceMemberService>>(),
            _authIdentity,
            Substitute.For<IWorkspaceEventPublisher>(),
            sharedConfiguration);

        var preview = await firstServiceInstance.PreviewMemberRoleChangeAsync(workspaceId, targetUserId, "Member", ownerUserId);
        Assert.True(preview.IsSuccess);

        var apply = await secondServiceInstance.ApplyMemberRoleChangeAsync(
            workspaceId,
            targetUserId,
            new ApplyWorkspaceRoleChangeRequest("Member", Guid.NewGuid().ToString("N"), preview.Value!.PreviewToken!),
            ownerUserId);

        Assert.True(apply.IsSuccess);
        Assert.Equal(memberRoleId, target.RoleId);
    }

    [Fact]
    public async Task PreviewMemberRoleChange_ShouldFailGracefully_WhenSigningKeyMissing()
    {
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId };
        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, MembershipType = "Internal" };
        var target = new WorkspaceMember { WorkspaceId = workspaceId, UserId = targetUserId, RoleId = memberRoleId, MembershipType = "Internal" };
        var serviceWithoutSigningKey = CreateMemberServiceWithoutPreviewSigningKey();

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(owner)), "", Arg.Any<CancellationToken>()).Returns(owner);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(target)), "", Arg.Any<CancellationToken>()).Returns(target);
        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(memberRoleId, "Member");

        var result = await serviceWithoutSigningKey.PreviewMemberRoleChangeAsync(workspaceId, targetUserId, "Admin", ownerUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.RolePreviewSigningKeyNotConfigured, result.Error);
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
    public async Task UpdateMemberAsync_ShouldFail_WhenAdminSelfGrantsMeetingPermission()
    {
        // This test previously asserted the opposite ("ShouldSucceed_WhenAdminUpdatesSelf"),
        // pinning the self-grant hole in place as though it were a feature. An Admin
        // whose meeting-hosting permission an Owner has just revoked could PATCH their
        // own row and restore it — the exact revocation WT-249 made a real enforcement
        // gate, and the beat the demo script builds to.
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
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.AdminCannotModifyPeerAdmin, result.Error);
        Assert.False(adminMember.CanCreateMeetings);
        _workspaceMemberRepository.DidNotReceiveWithAnyArgs().Update(Arg.Any<WorkspaceMember>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldSucceed_WhenOwnerRevokesAdminMeetingPermission()
    {
        // The demo beat, in the direction that must keep working: the Owner revokes.
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId, CanCreateMeetings = true };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: false);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);

        StubRoleName(ownerRoleId, "Owner");
        StubRoleName(adminRoleId, "Admin");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, adminUserId, request, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(adminMember.CanCreateMeetings);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldSucceed_WhenAdminUpdatesOrdinaryMember()
    {
        // Admins must keep their ordinary job: managing Members.
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var memberUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();

        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };
        var targetMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = memberUserId, RoleId = memberRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(adminMember)),
            "", Arg.Any<CancellationToken>()).Returns(adminMember);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(targetMember)),
            "", Arg.Any<CancellationToken>()).Returns(targetMember);

        StubRoleName(adminRoleId, "Admin");
        StubRoleName(memberRoleId, "Member");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, memberUserId, request, adminUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(targetMember.CanCreateMeetings);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_ShouldSucceed_WhenOwnerUpdatesSelf()
    {
        // Deliberately still allowed. ValidateMeetingCreationAsync reads
        // CanCreateMeetings with no Owner bypass and an Admin cannot edit an Owner, so
        // refusing this would strand a sole Owner who had switched their own hosting
        // off. The web client hides the control; the API does not need to.
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId, CanCreateMeetings = false };
        var request = new UpdateWorkspaceMemberRequest(CanCreateMeetings: true);

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Is<Expression<Func<WorkspaceMember, bool>>>(e => e.Compile()(ownerMember)),
            "", Arg.Any<CancellationToken>()).Returns(ownerMember);

        StubRoleName(ownerRoleId, "Owner");

        // Act
        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, ownerUserId, request, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(ownerMember.CanCreateMeetings);
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

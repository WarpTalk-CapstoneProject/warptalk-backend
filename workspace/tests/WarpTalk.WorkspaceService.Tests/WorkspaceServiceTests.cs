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
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
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
    private readonly IWorkspaceVerifiedDomainRepository _workspaceVerifiedDomainRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly IWorkspaceEventPublisher _eventPublisher;
    private readonly AppWorkspaceService _workspaceService;

    public WorkspaceServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceVerifiedDomainRepository = Substitute.For<IWorkspaceVerifiedDomainRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _workspaceCache = Substitute.For<IWorkspaceCacheService>();
        _eventPublisher = Substitute.For<IWorkspaceEventPublisher>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_workspaceVerifiedDomainRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_workspaceVerifiedDomainRepository);
        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        _workspaceService = new AppWorkspaceService(_unitOfWork, _workspaceCache, Substitute.For<ILogger<AppWorkspaceService>>(), _authIdentity, _eventPublisher);
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
        var request = new CreateWorkspaceRequest("DeepMind Team", "https://cdn.com/logo.png", RequireVerifiedDomainForInternal: true);

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
        await _workspaceVerifiedDomainRepository.Received(1).AddAsync(Arg.Is<WorkspaceVerifiedDomain>(vd => vd.Domain == "warptalk.vn" && vd.Status == "verified"), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenNameIsEmpty()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreateWorkspaceRequest("", null);

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
        var request = new CreateWorkspaceRequest("New Enterprise", null, RequireVerifiedDomainForInternal: true);

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Mock that they already belong to another Enterprise workspace as an internal member
        var otherEnterpriseWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"],\"RequireVerifiedDomainForInternal\":true}"
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

        _workspaceVerifiedDomainRepository.AnyAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

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
        var request = new CreateWorkspaceRequest("New Enterprise", null, RequireVerifiedDomainForInternal: true);

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
        await _workspaceVerifiedDomainRepository.Received(1).AddAsync(Arg.Is<WorkspaceVerifiedDomain>(vd =>
            vd.Domain == "enterprise.com" && vd.Status == "verified"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenDomainRegisteredElsewhere()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "user@company.com" };
        var request = new CreateWorkspaceRequest("New Work", null, RequireVerifiedDomainForInternal: true);

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _workspaceMemberRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Workspace", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());

        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Mock another active workspace verifying "company.com"
        var otherWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            Settings = "{\"VerifiedDomains\":[\"company.com\"]}"
        };
        _workspaceRepository.FindAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<Workspace> { otherWorkspace });

        var verifiedDomain = new WorkspaceVerifiedDomain
        {
            WorkspaceId = otherWorkspace.Id,
            Domain = "company.com",
            Status = "verified",
            Workspace = otherWorkspace
        };
        _workspaceVerifiedDomainRepository.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Is("Workspace"),
            Arg.Any<CancellationToken>())
            .Returns(verifiedDomain);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("corporate domain registered with another workspace", result.Error);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_WithoutVerifiedDomain_WhenNoDomainProvided()
    {
        // Arrange
        // NOTE: this test used to sign in as owner@gmail.com and assert success, which
        // pinned the public-domain hole in place rather than any intended behaviour.
        // A corporate account opting out of verified-domain classification is the real
        // case it was meant to cover, and that still works.
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@corp-example.com" };
        var request = new CreateWorkspaceRequest("Personal Team", "https://cdn.com/logo.png"); // No verified domains, RequireVerifiedDomainForInternal = null

        StubUser(userId, user);
        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Personal Team", result.Value.Name);

        // Verify we saved the workspace with empty verified domains and RequireVerifiedDomainForInternal = false
        await _workspaceRepository.Received(1).AddAsync(Arg.Is<Workspace>(w =>
            w.Settings.Contains("\"RequireVerifiedDomainForInternal\":false") &&
            w.Settings.Contains("\"VerifiedDomains\":[]")), Arg.Any<CancellationToken>());

        // Verify we did NOT add any WorkspaceVerifiedDomain records
        await _workspaceVerifiedDomainRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<WorkspaceVerifiedDomain>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_WithCustomVerifiedDomains()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@deepmind.com" };
        // NOTE: this test used to claim BOTH "deepmind.com" and "google.com" from a
        // @deepmind.com account and assert both were written. That is the domain-claim
        // hole, asserted as a feature. The legitimate half — a workspace claiming its
        // own domain — is what it now covers; the refusal is pinned separately in
        // CreateWorkspaceAsync_ShouldFail_WhenClaimingDomainTheCallerDoesNotOwn.
        var request = new CreateWorkspaceRequest(
            "DeepMind Labs",
            "https://cdn.com/logo.png",
            VerifiedDomains: new List<string> { "deepmind.com" },
            RequireVerifiedDomainForInternal: true
        );

        StubUser(userId, user);
        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);

        // Verify we saved the workspace with its own verified domain
        await _workspaceRepository.Received(1).AddAsync(Arg.Is<Workspace>(w =>
            w.Settings.Contains("\"RequireVerifiedDomainForInternal\":true") &&
            w.Settings.Contains("deepmind.com")), Arg.Any<CancellationToken>());

        // Verify the WorkspaceVerifiedDomain record was added for the caller's own domain
        await _workspaceVerifiedDomainRepository.Received(1).AddAsync(Arg.Is<WorkspaceVerifiedDomain>(vd => vd.Domain == "deepmind.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenDomainIsPublicDomain()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "owner@gmail.com" };
        var request = new CreateWorkspaceRequest(
            "Fake Google",
            "https://cdn.com/logo.png",
            VerifiedDomains: new List<string> { "gmail.com" }, // Public domain
            RequireVerifiedDomainForInternal: true
        );

        StubUser(userId, user);
        var ownerRole = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        StubRoleByName("Owner", ownerRole);

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        // The caller-eligibility gate now fires first, so the refusal names the account
        // rather than the claimed domain. Both refusals are still refusals.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.PublicEmailDomainCannotCreateWorkspace, result.Error);
    }

    // ── Hole 1: the public-email-domain block must not be switchable from the body ──

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateWorkspaceAsync_ShouldFail_ForPublicEmailDomain_WhateverRequireVerifiedDomainForInternalSays(bool? requireVerified)
    {
        // The bypass this pins: POST /workspaces {"requireVerifiedDomainForInternal": false}
        // from a gmail.com account used to create a workspace, because BOTH the
        // public-domain check and the already-Internal-elsewhere check sat inside
        // `if (requireVerified)` and requireVerified came from the request body.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "attacker@gmail.com" };
        var request = new CreateWorkspaceRequest("Free Workspace", null, RequireVerifiedDomainForInternal: requireVerified);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.PublicEmailDomainCannotCreateWorkspace, result.Error);
        await _workspaceRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenAlreadyInternalElsewhere_EvenWithRequireVerifiedDomainOff()
    {
        // The other half of Hole 1: the one-Enterprise-home-per-user rule was also
        // behind `if (requireVerified)`.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "employee@enterprise.com" };
        var request = new CreateWorkspaceRequest("Second Home", null, RequireVerifiedDomainForInternal: false);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        var otherEnterpriseWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Settings = "{\"VerifiedDomains\":[\"enterprise.com\"],\"RequireVerifiedDomainForInternal\":true}"
        };
        _workspaceMemberRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Is("Workspace"),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>
            {
                new WorkspaceMember { UserId = userId, Workspace = otherEnterpriseWorkspace, MembershipType = "Internal" }
            });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, result.Error);
        await _workspaceRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_ForCorporateDomain_WithRequireVerifiedDomainOff()
    {
        // The legitimate caller must still get through: a corporate account that opts
        // out of verified-domain classification is not what either gate is aimed at.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "founder@corp-example.com" };
        var request = new CreateWorkspaceRequest("Corp Team", null, RequireVerifiedDomainForInternal: false);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Corp Team", result.Value!.Name);
        await _workspaceRepository.Received(1).AddAsync(Arg.Is<Workspace>(w => w.OwnerId == userId), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Hole 2: a workspace may only claim a domain the caller's own email is on ──

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenClaimingDomainTheCallerDoesNotOwn()
    {
        // attacker.com claiming victimcorp.com would auto-classify every victimcorp.com
        // joiner as Internal — the trusted membership tier of a company the caller has
        // nothing to do with.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "attacker@attacker.com" };
        var request = new CreateWorkspaceRequest(
            "Totally Legit Corp",
            null,
            VerifiedDomains: new List<string> { "victimcorp.com" },
            RequireVerifiedDomainForInternal: true);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.CannotVerifyUnownedDomain, result.Error);
        await _workspaceVerifiedDomainRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<WorkspaceVerifiedDomain>(), Arg.Any<CancellationToken>());
        await _workspaceRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenClaimingAnUnownedDomainAlongsideItsOwn()
    {
        // The smuggling variant: one real domain to look legitimate, one stolen.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "attacker@attacker.com" };
        var request = new CreateWorkspaceRequest(
            "Mixed Claims",
            null,
            VerifiedDomains: new List<string> { "attacker.com", "victimcorp.com" },
            RequireVerifiedDomainForInternal: true);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotVerifyUnownedDomain, result.Error);
        await _workspaceVerifiedDomainRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<WorkspaceVerifiedDomain>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldFail_WhenClaimingUnownedDomain_EvenWithRequireVerifiedDomainOff()
    {
        // With requireVerified off no workspace_verified_domains row is written, but the
        // list is still persisted into the settings JSON, so the claim is still
        // validated. This also pins that the ownership rule is not itself gated on the
        // request-body flag.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "attacker@attacker.com" };
        var request = new CreateWorkspaceRequest(
            "Quiet Claim",
            null,
            VerifiedDomains: new List<string> { "victimcorp.com" },
            RequireVerifiedDomainForInternal: false);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotVerifyUnownedDomain, result.Error);
        await _workspaceRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateWorkspaceAsync_ShouldSucceed_WhenClaimingItsOwnDomain_IgnoringCaseAndWhitespace()
    {
        // The legitimate caller: a workspace claiming the domain it actually belongs to.
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "founder@Corp-Example.com" };
        var request = new CreateWorkspaceRequest(
            "Corp Example",
            null,
            VerifiedDomains: new List<string> { "  CORP-EXAMPLE.COM  " },
            RequireVerifiedDomainForInternal: true);

        StubUser(userId, user);
        StubRoleByName("Owner", new Role { Id = Guid.NewGuid(), Name = "Owner" });

        // Act
        var result = await _workspaceService.CreateWorkspaceAsync(request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        await _workspaceVerifiedDomainRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceVerifiedDomain>(vd => vd.Domain == "corp-example.com" && vd.Status == "verified"),
            Arg.Any<CancellationToken>());
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
        Assert.NotNull(result.Value);
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
            RoleId = roleId,
            MembershipType = MembershipType.Internal.ToString()
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                Id = workspaceId,
                Name = "DeepMind",
                Slug = "deepmind",
                IsActive = true,
                Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}"
            });

        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "test@warptalk.vn" });

        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        _workspaceVerifiedDomainRepository.AnyAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(workspaceId, result.Value.SelectedWorkspaceId);

        // Verify cache service received the update
        await _workspaceCache.Received(1).SetActiveWorkspaceDetailsAsync(userId, workspaceId, "Member", "Internal", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldCacheStoredMembershipType_WhenUserIsExternalMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = MembershipType.External.ToString()
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                Id = workspaceId,
                Name = "DeepMind",
                Slug = "deepmind",
                IsActive = true,
                Settings = "{\"VerifiedDomains\":[],\"RequireVerifiedDomainForInternal\":false}"
            });

        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        await _workspaceCache.Received(1).SetActiveWorkspaceDetailsAsync(userId, workspaceId, "Member", "External", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WithNotFound_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.WorkspaceNotFound, result.Error);
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WithNotFound_WhenMembershipIsSuspended()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var suspendedMember = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = Guid.NewGuid(),
            Status = WorkspaceMemberStatus.Suspended.ToStorageValue()
        };

        _workspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<WorkspaceMember, bool>>>().Compile();
                return predicate(suspendedMember) ? suspendedMember : null;
            });

        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _workspaceCache.DidNotReceive().SetActiveWorkspaceDetailsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WhenWorkspaceMissing()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid() });

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((Workspace?)null);

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _workspaceCache.DidNotReceive().SetActiveWorkspaceDetailsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Membership rows are not cleared when a workspace is soft-deleted, so the member lookup above
    // still succeeds here. Without the DeletedAt check this call would happily cache a dead
    // workspace as the user's active context.
    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WhenWorkspaceIsSoftDeleted()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid() });

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                Id = workspaceId,
                Name = "DeepMind",
                Slug = "deepmind",
                IsActive = true,
                DeletedAt = DateTime.UtcNow,
                Settings = "{}"
            });

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _workspaceCache.DidNotReceive().SetActiveWorkspaceDetailsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectWorkspaceAsync_ShouldFail_WhenWorkspaceIsDeactivated()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid() });

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                Id = workspaceId,
                Name = "DeepMind",
                Slug = "deepmind",
                IsActive = false,
                Settings = "{}"
            });

        // Act
        var result = await _workspaceService.SelectWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.WorkspaceInactive, result.Error);
        await _workspaceCache.DidNotReceive().SetActiveWorkspaceDetailsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task GetWorkspaceByIdAsync_ShouldFail_WithNotFound_WhenUserIsNotMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.WorkspaceNotFound, result.Error);
    }

    [Fact]
    public async Task GetWorkspaceByIdAsync_ShouldFail_WithNotFound_WhenMembershipIsSuspended()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var suspendedMember = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = Guid.NewGuid(),
            Status = WorkspaceMemberStatus.Suspended.ToStorageValue()
        };

        _workspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<WorkspaceMember, bool>>>().Compile();
                return predicate(suspendedMember) ? suspendedMember : null;
            });

        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
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
            .Returns((Workspace?)null);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task GetWorkspaceByIdForAdminAsync_ShouldReturnWorkspaceWithoutMembership()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "FitPick",
            Slug = "fitpick",
            CreatedAt = DateTime.UtcNow
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdForAdminAsync(workspaceId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("FitPick", result.Value.Name);
        Assert.Equal("admin", result.Value.Role);
        await _workspaceMemberRepository.DidNotReceive().FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetWorkspaceByIdForAdminAsync_ShouldFail_WhenWorkspaceNotFound()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((Workspace?)null);

        // Act
        var result = await _workspaceService.GetWorkspaceByIdForAdminAsync(workspaceId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    #endregion

    #region Workspace Settings Tests

    [Fact]
    public async Task GetWorkspaceSettingsAsync_ShouldReturnParsedSettings_WhenUserIsAdmin()
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
        _authIdentity.GetRoleByIdAsync(member.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = member.RoleId, Name = "Admin" });

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
    public async Task GetWorkspaceSettingsAsync_ShouldReturnDefaultSettings_WhenUserIsOwner()
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
        _authIdentity.GetRoleByIdAsync(member.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = member.RoleId, Name = "Owner" });

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
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _workspaceService.GetWorkspaceSettingsAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task GetWorkspaceSettingsAsync_ShouldFail_WhenUserIsRegularMember()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(member);
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });

        var result = await _workspaceService.GetWorkspaceSettingsAsync(workspaceId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _workspaceRepository.DidNotReceive()
            .GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldSucceed_WhenOwnerChangesAllowExternalCollaboration()
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
            new List<string> { "warptalk.vn" },
            true,
            true,
            new AiUsagePolicyDto(
                true,
                new PiiRedactionDto(true),
                new DlpDto(true, new List<string> { "confidential" }),
                new TranslationProfileDto(
                    "professional",
                    new LanguageSpecificRulesDto("formal_hierarchical", "keigo_teineigo"))),
            false
        );

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, AllowExternalCollaboration = true });
        _authIdentity.GetUserByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, Email = "admin@warptalk.vn" });

        var memberRoleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Owner" });
        _workspaceMemberRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { member });

        _workspaceRepository.UpdateSettingsAsync(workspaceId, Arg.Any<WorkspaceConfiguration>(), userId, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.True(result.IsSuccess);
        await _workspaceRepository.Received(1).UpdateSettingsAsync(
            workspaceId,
            Arg.Is<WorkspaceConfiguration>(c =>
                c.DefaultLanguage == "vi"
                && c.AiUsagePolicy != null
                && c.AiUsagePolicy.RedactPii != null
                && c.AiUsagePolicy.RedactPii.Enabled
                && c.AiUsagePolicy.Dlp != null
                && c.AiUsagePolicy.Dlp.Enabled
                && c.AiUsagePolicy.Dlp.KeywordsBlacklist != null
                && c.AiUsagePolicy.Dlp.KeywordsBlacklist.Contains("confidential")),
            userId,
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldSucceed_WhenUserIsAdmin()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var admin = new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, RoleId = adminRoleId };

        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"AllowExternalCollaboration\":true,\"RequireVerifiedDomainForInternal\":false,\"ArtifactRetentionDays\":30}"
        };
        var requested = new WorkspaceSettingsDto(
            "en", "UTC", new List<string>(), true, 5, 30,
            new List<string>(), true, false, null, false);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(admin);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminRoleId, Name = "Admin" });
        _workspaceRepository.UpdateSettingsAsync(Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, requested, userId);

        Assert.True(result.IsSuccess);
        await _workspaceRepository.Received(1).UpdateSettingsAsync(
            workspaceId, Arg.Any<WorkspaceConfiguration>(), userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenAdminChangesAllowExternalCollaboration()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var admin = new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, RoleId = adminRoleId };
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"AllowExternalCollaboration\":true,\"RequireVerifiedDomainForInternal\":false,\"ArtifactRetentionDays\":30}"
        };
        var requested = new WorkspaceSettingsDto(
            "en", "UTC", new List<string>(), true, 5, 30,
            new List<string>(), false, false, null, false);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(admin);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminRoleId, Name = "Admin" });

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, requested, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanModifyPolicySettings, result.Error);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenUserIsNotOwnerOrAdmin()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceSettingsDto(
            "vi",
            "Asia/Ho_Chi_Minh",
            new List<string>(),
            false,
            5,
            30,
            new List<string>(),
            true,
            true,
            null,
            false
        );

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, AllowExternalCollaboration = true });
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

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenDomainIsPublicDomain()
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
            new List<string> { "yahoo.com" }, // Public domain
            true,
            true,
            null,
            false
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
            .Returns(new Role { Id = memberRoleId, Name = "Owner" });

        // Act
        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.CannotVerifyPublicDomain, result.Error);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenStrictDomainVerificationHasNoDomains()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var settings = new WorkspaceSettingsDto(
            "en",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            new List<string>(),
            true,
            true,
            null,
            false);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, AllowExternalCollaboration = true });
        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = ownerRoleId });
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, settings, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.VerifiedDomainsRequired, result.Error);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateWorkspaceSettingsAsync_ShouldFail_WhenRemovedDomainHasActiveInternalMembers()
    {
        var userId = Guid.NewGuid();
        var activeMemberUserId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();
        var owner = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = ownerRoleId };
        var activeInternalMember = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = activeMemberUserId,
            MembershipType = MembershipType.Internal.ToString()
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"VerifiedDomains\":[\"company.com\"],\"AllowExternalCollaboration\":true}"
        };
        var requested = new WorkspaceSettingsDto(
            "en", "UTC", new List<string>(), true, 5, 30,
            new List<string>(), false, false, null, false);

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(owner);
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });
        _workspaceMemberRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { activeInternalMember });
        _authIdentity.GetUserByIdAsync(activeMemberUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = activeMemberUserId, Email = "member@company.com" });

        var result = await _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, requested, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.CannotRevokeDomainWithActiveMembers, result.Error);
        await _workspaceRepository.DidNotReceive().UpdateSettingsAsync(
            Arg.Any<Guid>(), Arg.Any<WorkspaceConfiguration>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region SoftDeleteWorkspaceAsync Tests

    [Fact]
    public async Task SoftDeleteWorkspaceAsync_ShouldSucceed_AndPublishWorkspaceDeletedEvent_WhenRequesterIsOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId, OwnerId = ownerUserId };
        var ownerMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = ownerUserId, RoleId = ownerRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ownerMember);
        _authIdentity.GetRoleByIdAsync(ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = ownerRoleId, Name = "Owner" });

        // Act
        var result = await _workspaceService.SoftDeleteWorkspaceAsync(workspaceId, ownerUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(workspace.DeletedAt);
        Assert.Equal(ownerUserId, workspace.UpdatedBy);

        _workspaceRepository.Received(1).Update(workspace);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishWorkspaceDeletedAsync(workspaceId, ownerUserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SoftDeleteWorkspaceAsync_ShouldFail_WhenRequesterIsNotOwner()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        var workspace = new Workspace { Id = workspaceId, OwnerId = Guid.NewGuid() };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminUserId, RoleId = adminRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(adminMember);
        _authIdentity.GetRoleByIdAsync(adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminRoleId, Name = "Admin" });

        // Act
        var result = await _workspaceService.SoftDeleteWorkspaceAsync(workspaceId, adminUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanDeleteWorkspace, result.Error);
        Assert.Null(workspace.DeletedAt);

        _workspaceRepository.DidNotReceive().Update(Arg.Any<Workspace>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceive().PublishWorkspaceDeletedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SoftDeleteWorkspaceAsync_ShouldFail_WhenWorkspaceNotFound()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((Workspace?)null);

        // Act
        var result = await _workspaceService.SoftDeleteWorkspaceAsync(workspaceId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.WorkspaceNotFound, result.Error);

        _workspaceRepository.DidNotReceive().Update(Arg.Any<Workspace>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceive().PublishWorkspaceDeletedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    #endregion
}

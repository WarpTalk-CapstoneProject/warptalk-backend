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
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceInvitationServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceInvitationRepository _workspaceInvitationRepository;
    private readonly IWorkspaceVerifiedDomainRepository _workspaceVerifiedDomainRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceInvitationEmailComposer _emailComposer;
    private readonly WorkspaceInvitationService _workspaceInvitationService;

    public WorkspaceInvitationServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceInvitationRepository = Substitute.For<IWorkspaceInvitationRepository>();
        _workspaceVerifiedDomainRepository = Substitute.For<IWorkspaceVerifiedDomainRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _emailComposer = Substitute.For<IWorkspaceInvitationEmailComposer>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceInvitationRepository.Returns(_workspaceInvitationRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_workspaceVerifiedDomainRepository);
        _unitOfWork.Repository<WorkspaceVerifiedDomain>().Returns(_workspaceVerifiedDomainRepository);
        _emailComposer.SendInvitationEmailAsync(
                Arg.Any<WorkspaceInvitation>(),
                Arg.Any<Workspace>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "message-id", null));

        _workspaceInvitationService = new WorkspaceInvitationService(
            _unitOfWork, 
            Substitute.For<ILogger<WorkspaceInvitationService>>(), 
            _authIdentity,
            _translationRoomClient,
            _emailComposer);
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
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", AllowExternalCollaboration = true, RequireVerifiedDomainForInternal = true };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);
        StubUserEmail("invitee@warptalk.vn", Guid.NewGuid());

        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>
            {
                new()
                {
                    WorkspaceId = workspaceId,
                    Domain = "warptalk.vn",
                    Status = "verified",
                    VerifiedAt = DateTime.UtcNow
                }
            });

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member", "Internal");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("invitee@warptalk.vn", result.Value.Invitation.Email);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldReturnConflict_WhenActivePendingInvitationExists()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws", AllowExternalCollaboration = true, RequireVerifiedDomainForInternal = true };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };
        
        var oldInvitation = new WorkspaceInvitation 
        { 
            Id = Guid.NewGuid(), 
            WorkspaceId = workspaceId, 
            Email = "invitee@warptalk.vn", 
            Status = InvitationStatus.PENDING.ToString(), 
            RoleId = roleId,
            MembershipType = "Internal",
            ExpiresAt = DateTime.UtcNow.AddDays(1) 
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _workspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, "invitee@warptalk.vn", Arg.Any<CancellationToken>()).Returns(oldInvitation);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);
        StubUserEmail("invitee@warptalk.vn", Guid.NewGuid());

        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>
            {
                new()
                {
                    WorkspaceId = workspaceId,
                    Domain = "warptalk.vn",
                    Status = "verified",
                    VerifiedAt = DateTime.UtcNow
                }
            });

        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member", "Internal");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        _workspaceInvitationRepository.DidNotReceive().Update(oldInvitation);
        await _workspaceInvitationRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
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
            RequireVerifiedDomainForInternal = true
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var request = new InviteMemberRequest("external@gmail.com", "Member", "External");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldFail_WhenExternalInvitationHasNonMemberRole()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            RequireVerifiedDomainForInternal = true
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        
        StubRoleName(inviterMember.RoleId, "Owner");
        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        // Try to invite as Admin under External membership type
        var request = new InviteMemberRequest("external@gmail.com", "Admin", "External");

        // Act
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.ExternalMemberMustHaveMemberRole, result.Error);
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
    public async Task AcceptInvitationAsync_ShouldFail_WhenInvitationIsExpired()
    {
        // Arrange
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Email = "invitee@warptalk.vn",
            Status = InvitationStatus.PENDING.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(-1) // Expired
        };

        _workspaceInvitationRepository.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invitation);

        var request = new AcceptInvitationRequest("expired_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.InvitationExpired, result.Error);
        Assert.Equal(InvitationStatus.EXPIRED.ToString(), invitation.Status);
        _workspaceInvitationRepository.Received(1).Update(invitation);
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
            MembershipType = MembershipType.Internal.ToString(),
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

        _workspaceVerifiedDomainRepository.AnyAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

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
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(new Workspace { Id = workspaceId, Settings = "{\"VerifiedDomains\":[\"warptalk.vn\"]}" });
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns((WorkspaceMember?)null);

        _workspaceVerifiedDomainRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        _workspaceInvitationRepository.Received(1).Update(invitation);
        await _workspaceMemberRepository.Received(1).AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    // WT-179 — the acceptance gate used to read invitation.MembershipType, which
    // ProcessAcceptInvitationAsync overwrites moments later, and to treat a workspace that
    // merely LISTS verified domains as one that REQUIRES them. Together those two facts made
    // every invitation to `testworkspace` on production permanently unacceptable.

    /// <summary>Exactly production's `testworkspace`: policy flag off, one stale verified
    /// domain in the settings JSON, and an invitee from a different domain.</summary>
    private WorkspaceInvitation ArrangeWt179Repro(
        Guid workspaceId,
        string userEmail,
        string storedMembershipType,
        bool requireVerifiedDomainForInternal = false,
        bool allowExternalCollaboration = true)
    {
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = allowExternalCollaboration,
            RequireVerifiedDomainForInternal = requireVerifiedDomainForInternal,
            Settings = "{\"VerifiedDomains\":[\"warptalk.io.vn\"],\"RequireVerifiedDomainForInternal\":false}"
        };

        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RoleId = Guid.NewGuid(),
            Email = userEmail,
            Status = InvitationStatus.PENDING.ToString(),
            MembershipType = storedMembershipType,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        _workspaceInvitationRepository.GetByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // The invitee's domain is not in the verified-domain table, which is what used to make
        // the gate fire.
        _workspaceVerifiedDomainRepository.AnyAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        return invitation;
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldSucceed_WhenTheWorkspaceListsVerifiedDomainsButDoesNotRequireThem()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(workspaceId, userEmail, MembershipType.Internal.ToString());

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        // Before the fix this was 400 VALIDATION_ERROR
        // "Cannot invite as an Internal member because the email domain is not verified".
        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        // Flags off means this workspace does not separate internal from external at all, so the
        // derived type is Internal — and that is what the membership must be created with.
        Assert.Equal(MembershipType.Internal.ToString(), invitation.MembershipType);
        await _workspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(m => m.MembershipType == MembershipType.Internal.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldIgnoreTheStoredMembershipType_AndUseTheDerivedOne()
    {
        // The stored value is stale by construction — acceptance recomputes it. Pinning this
        // keeps the gate and the recomputation from drifting apart again.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(workspaceId, userEmail, MembershipType.External.ToString());

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipType.Internal.ToString(), invitation.MembershipType);
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldAdmitAsExternal_WhenThePolicyIsOnAndTheDomainIsNotVerified()
    {
        // With the policy actually on, an unverified domain resolves to External rather than
        // being refused — which is why the Internal-without-verified-domain rejection is now
        // unreachable through the derive path and kept only as an invariant guard.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(
            workspaceId,
            userEmail,
            MembershipType.Internal.ToString(),
            requireVerifiedDomainForInternal: true);

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipType.External.ToString(), invitation.MembershipType);
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldFail_WhenThePolicyIsOnAndExternalCollaborationIsDisabled()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(
            workspaceId,
            userEmail,
            MembershipType.Internal.ToString(),
            requireVerifiedDomainForInternal: true,
            allowExternalCollaboration: false);

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
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

        _workspaceVerifiedDomainRepository.AnyAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId, userEmail);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    #endregion

    #region Join Request Tests

    [Fact]
    public async Task CreateJoinRequestAsync_ShouldClassifyAsExternal_WhenWorkspaceHasNoVerifiedDomain()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = "user@gmail.com";
        var workspace = new Workspace
        {
            Id = workspaceId,
            Slug = "small-workspace",
            IsActive = true,
            RequireVerifiedDomainForInternal = true,
            AllowExternalCollaboration = true
        };

        _workspaceRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(workspace);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Member" });
        _workspaceInvitationRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceInvitation, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceInvitation?)null);
        _workspaceVerifiedDomainRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var result = await _workspaceInvitationService.CreateJoinRequestAsync(
            new CreateJoinRequestCommand(null, workspace.Slug), userId, email);

        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.REQUESTED.ToString(), result.Value!.Status);
        Assert.Equal(MembershipType.External.ToString(), result.Value.MembershipType);

        await _workspaceInvitationRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceInvitation>(invitation =>
                invitation.RequestedBy == userId &&
                invitation.InvitedBy == userId &&
                invitation.RoleId == roleId &&
                invitation.MembershipType == MembershipType.External.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveJoinRequestAsync_ShouldAcceptAndCreateMemberWithSelectedMembershipType()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = "requester@gmail.com",
            RequestedBy = requesterId,
            InvitedBy = requesterId,
            RoleId = memberRoleId,
            MembershipType = MembershipType.External.ToString(),
            Status = InvitationStatus.REQUESTED.ToString(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        var workspace = new Workspace { Id = workspaceId, Name = "Acme", Slug = "acme", AllowExternalCollaboration = true };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminId, RoleId = Guid.NewGuid() };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember, (WorkspaceMember?)null);
        _authIdentity.GetRoleByIdAsync(adminMember.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminMember.RoleId, Name = "Owner" });
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _authIdentity.GetRoleByIdAsync(memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Member" });
        _authIdentity.GetRoleByNameAsync("Member", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = memberRoleId, Name = "Member" });
        _emailComposer.SendJoinRequestApprovedEmailAsync(
            Arg.Any<WorkspaceInvitation>(), Arg.Any<Workspace>(), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "approval-message", null));

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(
            workspaceId,
            invitationId,
            adminId,
            new ApproveJoinRequestRequest(MembershipType.External.ToString()));

        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        Assert.Equal(MembershipType.External.ToString(), invitation.MembershipType);
        Assert.Equal(adminId, invitation.ReviewedBy);
        Assert.NotNull(invitation.ReviewedAt);
        Assert.NotNull(invitation.AcceptedAt);
        await _workspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(member =>
                member.UserId == requesterId &&
                member.RoleId == memberRoleId &&
                member.Status == WorkspaceMemberStatus.Active.ToString() &&
                member.MembershipType == MembershipType.External.ToString()),
            Arg.Any<CancellationToken>());
        await _emailComposer.Received(1).SendJoinRequestApprovedEmailAsync(invitation, workspace, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectJoinRequestAsync_ShouldUseRejectedStatusAndReviewerTracking()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminId, RoleId = Guid.NewGuid() };
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Status = InvitationStatus.REQUESTED.ToString(),
            MembershipType = MembershipType.External.ToString(),
            Email = "requester@gmail.com"
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember);
        _authIdentity.GetRoleByIdAsync(adminMember.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminMember.RoleId, Name = "Admin" });
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);

        var result = await _workspaceInvitationService.RejectJoinRequestAsync(workspaceId, invitationId, adminId);

        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.REJECTED.ToString(), invitation.Status);
        Assert.Equal(adminId, invitation.ReviewedBy);
        Assert.NotNull(invitation.ReviewedAt);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    #endregion
}

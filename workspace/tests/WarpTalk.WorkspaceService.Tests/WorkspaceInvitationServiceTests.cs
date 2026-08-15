using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;
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
        _billingSubscriptionClient = Substitute.For<IBillingSubscriptionClient>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceInvitationRepository.Returns(_workspaceInvitationRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_workspaceVerifiedDomainRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_workspaceVerifiedDomainRepository);
        _emailComposer.SendInvitationEmailAsync(
                Arg.Any<WorkspaceInvitation>(),
                Arg.Any<Workspace>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "message-id", null));
        _billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        _workspaceInvitationService = new WorkspaceInvitationService(
            _unitOfWork,
            Substitute.For<ILogger<WorkspaceInvitationService>>(),
            _authIdentity,
            _translationRoomClient,
            _emailComposer,
            _billingSubscriptionClient,
            new WorkspaceInvitationAcceptanceProcessor(_unitOfWork, _billingSubscriptionClient, _authIdentity));
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

    /// <summary>
    /// Puts domains in the workspace_verified_domains table — the only place the policy checks
    /// now read from.
    /// </summary>
    private void StubVerifiedDomains(Guid workspaceId, params string[] domains)
    {
        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(domains.Select(d => new WorkspaceVerifiedDomain
            {
                WorkspaceId = workspaceId,
                Domain = d,
                Status = "verified",
                VerifiedAt = DateTime.UtcNow
            }).ToList());
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
    public async Task InviteMemberAsync_ShouldSendEmailWithInviterNameAndInvitationToken()
    {
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, Name = "Business WS", Slug = "business-ws" };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };
        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member", "Internal");

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);
        _authIdentity.GetUserByIdAsync(inviterUserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = inviterUserId, FullName = "Real Inviter", Email = "owner@warptalk.vn" });

        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.True(result.IsSuccess);
        await _emailComposer.Received(1).SendInvitationEmailAsync(
            Arg.Any<WorkspaceInvitation>(),
            workspace,
            "Real Inviter",
            "Member",
            Arg.Is<string>(token => token.Length == 64),
            Arg.Any<CancellationToken>());
        await _authIdentity.DidNotReceive().GetUserByEmailAsync("invitee@warptalk.vn", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldUseConfiguredExpiryDaysAndPersistTokenHash()
    {
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Business WS",
            Slug = "business-ws",
            Settings = "{\"InvitationExpiryDays\":3}"
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };
        var request = new InviteMemberRequest("invitee@warptalk.vn", "Member", "Internal");
        WorkspaceInvitation? addedInvitation = null;

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _workspaceInvitationRepository
            .AddAsync(Arg.Do<WorkspaceInvitation>(invitation => addedInvitation = invitation), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);

        var before = DateTime.UtcNow;
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);
        var after = DateTime.UtcNow;

        Assert.True(result.IsSuccess);
        Assert.NotNull(addedInvitation);
        Assert.False(string.IsNullOrWhiteSpace(addedInvitation!.TokenHash));
        Assert.InRange(
            addedInvitation.ExpiresAt,
            before.AddDays(3).AddSeconds(-1),
            after.AddDays(3).AddSeconds(1));
    }

    /// <summary>
    /// WT-375. The Owner turned External collaboration on precisely so this person could join,
    /// and the invitation sent before that is now unacceptable: it was stored Internal (the
    /// workspace had no external policy then) and Internal needs a verified domain, which a
    /// public gmail address can never have.
    ///
    /// Acceptance refuses it and tells the Owner to revoke it and send a new one — and sending a
    /// new one was refused because the dead invitation was still PENDING. There was no UI
    /// anywhere to break that loop. A re-invite now supersedes an invitation that can no longer
    /// be accepted.
    /// </summary>
    [Fact]
    public async Task InviteMemberAsync_ShouldSupersedeAPendingInvitation_ThatCanNoLongerBeAccepted()
    {
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Kim",
            Slug = "kim",
            AllowExternalCollaboration = true,
            RequireVerifiedDomainForInternal = true,
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        var stranded = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = "nh@gmail.com",
            Status = InvitationStatus.PENDING.ToString(),
            RoleId = roleId,
            MembershipType = "Internal",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _workspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, "nh@gmail.com", Arg.Any<CancellationToken>()).Returns(stranded);

        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleName(roleId, "Member");
        StubRoleId("Member", roleId);
        StubUserEmail("nh@gmail.com", Guid.NewGuid());

        _workspaceVerifiedDomainRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var request = new InviteMemberRequest("nh@gmail.com", "Member", "External");

        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(InvitationStatus.REVOKED.ToString(), stranded.Status);
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

    /// <summary>
    /// An Owner-led workspace with the verified-domain policy on and one verified domain.
    /// </summary>
    private Workspace ArrangeInviter(
        Guid workspaceId,
        Guid inviterUserId,
        Guid roleId,
        bool requireVerifiedDomainForInternal = true,
        bool allowExternalCollaboration = true,
        bool allowSubdomains = false,
        string roleName = "Member",
        params string[] verifiedDomains)
    {
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "WS",
            Slug = "ws",
            AllowExternalCollaboration = allowExternalCollaboration,
            RequireVerifiedDomainForInternal = requireVerifiedDomainForInternal,
            AllowSubdomains = allowSubdomains
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId(roleName, roleId);
        StubVerifiedDomains(workspaceId, verifiedDomains);

        return workspace;
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldStoreInternal_ForASubdomainAddress_WhenSubdomainsAreAllowed()
    {
        // Bug 1's create half. Accepting the same invitation is covered by
        // AcceptInvitationByIdAsync_ShouldAdmitASubdomainAddress_WhenSubdomainsAreAllowed — the
        // pair is the point, since the two paths used to match domains differently and only the
        // create side honoured AllowSubdomains.
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        ArrangeInviter(workspaceId, inviterUserId, roleId, allowSubdomains: true, verifiedDomains: "company.com");

        var request = new InviteMemberRequest("a@eng.company.com", "Member", "Internal");
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.True(result.IsSuccess);
        await _workspaceInvitationRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceInvitation>(i => i.MembershipType == MembershipType.Internal.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldStoreExternal_WhenTheInviterPicksItForAVerifiedDomainAddress()
    {
        // BR-140-011. The inference would have said Internal here; the inviter's choice wins.
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        ArrangeInviter(workspaceId, inviterUserId, roleId, verifiedDomains: "company.com");

        var request = new InviteMemberRequest("contractor@company.com", "Member", "External");
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.True(result.IsSuccess);
        await _workspaceInvitationRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceInvitation>(i => i.MembershipType == MembershipType.External.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldRejectInternalPublicDomain_WithItsOwnErrorCode()
    {
        // Bug 3. Sharing the unverified-domain message would send the inviter off to verify
        // gmail.com, which is not a thing that can happen.
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        ArrangeInviter(workspaceId, inviterUserId, roleId, verifiedDomains: "company.com");

        var request = new InviteMemberRequest("someone@gmail.com", "Member", "Internal");
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.CannotInviteInternalWithPublicDomain, result.Error);
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldAllowInternalPublicDomain_WhenTheDomainPolicyIsOff()
    {
        // BR-140-005. The public-domain rule is a special case of the verified-domain rule, not
        // a standalone one — with the policy off there is nothing to enforce.
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        ArrangeInviter(workspaceId, inviterUserId, roleId, requireVerifiedDomainForInternal: false);

        var request = new InviteMemberRequest("someone@gmail.com", "Member", "Internal");
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.True(result.IsSuccess);
        await _workspaceInvitationRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceInvitation>(i => i.MembershipType == MembershipType.Internal.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldReject_WhenMembershipTypeIsNotRecognised()
    {
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        ArrangeInviter(workspaceId, inviterUserId, roleId, verifiedDomains: "company.com");

        var request = new InviteMemberRequest("someone@company.com", "Member", "Contractor");
        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, inviterUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.InvalidMembershipType, result.Error);
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldAdmitASubdomainAddress_WhenSubdomainsAreAllowed()
    {
        // Bug 1's accept half — this returned CannotInviteInternalWithoutVerifiedDomain before,
        // leaving an invitation that could be created and never used.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "a@eng.company.com";

        var workspace = new Workspace
        {
            Id = workspaceId,
            RequireVerifiedDomainForInternal = true,
            AllowSubdomains = true
        };
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RoleId = Guid.NewGuid(),
            Email = userEmail,
            Status = InvitationStatus.PENDING.ToString(),
            MembershipType = MembershipType.Internal.ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        _workspaceInvitationRepository.GetByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);
        StubVerifiedDomains(workspaceId, "company.com");

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        await _workspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(m => m.MembershipType == MembershipType.Internal.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InviteMemberAsync_ShouldFail_WhenTrialWorkspaceMemberLimitReached()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var inviterUserId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Trial WS",
            Slug = "trial-ws",
            AllowExternalCollaboration = true,
            RequireVerifiedDomainForInternal = true
        };
        var inviterMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = inviterUserId, RoleId = Guid.NewGuid() };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>()).Returns(inviterMember);
        _workspaceMemberRepository.CountActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(4);
        _workspaceInvitationRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceInvitation, bool>>>(),
                "",
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceInvitation>
            {
                new()
                {
                    WorkspaceId = workspaceId,
                    Email = "pending@warptalk.vn",
                    Status = InvitationStatus.PENDING.ToString(),
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                }
            });
        _billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(true);

        StubRoleName(inviterMember.RoleId, "Owner");
        StubRoleId("Member", roleId);

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
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.TrialWorkspaceMemberLimitReached, result.Error);
        await _workspaceInvitationRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceInvitation>(), Arg.Any<CancellationToken>());
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

        // Enterprise-ness comes off the column, not off a VerifiedDomains list in the settings
        // JSON — a stale JSON list is not evidence of live policy (WT-179).
        var workspace = new Workspace
        {
            Id = workspaceId,
            RequireVerifiedDomainForInternal = true
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
        StubVerifiedDomains(workspaceId, "enterprise.com");

        // Mock that they already belong to another Enterprise workspace as an internal member
        var otherEnterpriseWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            RequireVerifiedDomainForInternal = true
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
    public async Task AcceptInvitationByIdAsync_ShouldHonourTheStoredMembershipType_NotRecomputeIt()
    {
        // BR-140-013. The stored value is the inviter's decision; acceptance admits it or
        // refuses, and never rewrites it. This used to assert the opposite — that a stored
        // External became Internal — which is the same rewrite that let an invitation issued as
        // Internal/Admin arrive as External/Admin once its domain lost verification.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(workspaceId, userEmail, MembershipType.External.ToString());

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipType.External.ToString(), invitation.MembershipType);
        await _workspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(m => m.MembershipType == MembershipType.External.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldReject_WhenThePolicyIsOnAndTheDomainIsNotVerified()
    {
        // The old behaviour quietly downgraded this invitation to External and admitted it.
        // Admitting an access class nobody approved is the thing BR-140-013 forbids, so the
        // stale intent is refused instead — and the invitation is left PENDING so an Owner can
        // still see it and decide (BR-140-014).
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(
            workspaceId,
            userEmail,
            MembershipType.Internal.ToString(),
            requireVerifiedDomainForInternal: true);

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        // hotmail.com can never be verified, so the invitee is told that rather than being sent
        // off to verify a domain nobody can verify.
        Assert.Contains(WorkspaceConstants.Errors.CannotInviteInternalWithPublicDomain, result.Error);
        Assert.Equal(InvitationStatus.PENDING.ToString(), invitation.Status);
        Assert.Equal(MembershipType.Internal.ToString(), invitation.MembershipType);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldReject_WhenTheVerifiedDomainWasRemovedAfterTheInviteWasSent()
    {
        // The privilege leak in full: issued Internal + Admin while company.com was verified,
        // then the domain is revoked. The old code recomputed External, kept RoleId untouched,
        // and created an External member holding Admin — the exact pairing the create path
        // refuses. Nothing is created now.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "employee@company.com";
        var invitation = ArrangeWt179Repro(
            workspaceId,
            userEmail,
            MembershipType.Internal.ToString(),
            requireVerifiedDomainForInternal: true);
        _authIdentity.GetRoleByIdAsync(invitation.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = invitation.RoleId, Name = "Admin" });

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, result.Error);
        Assert.Equal(InvitationStatus.PENDING.ToString(), invitation.Status);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldReject_WhenStoredExternalAndExternalCollaborationIsDisabled()
    {
        // Loosening or tightening AllowExternalCollaboration after the fact must not admit
        // someone the current settings would refuse.
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userEmail = "dolar@hotmail.com";
        var invitation = ArrangeWt179Repro(
            workspaceId,
            userEmail,
            MembershipType.External.ToString(),
            requireVerifiedDomainForInternal: true,
            allowExternalCollaboration: false);

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitation.Id, userId, userEmail);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Contains(WorkspaceConstants.Errors.ExternalCollaborationNotAllowed, result.Error);
        Assert.Equal(InvitationStatus.PENDING.ToString(), invitation.Status);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldFail_WhenTrialWorkspaceAlreadyHasFiveMembers()
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
        _workspaceMemberRepository.CountActiveMembersByWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(WorkspaceConstants.TrialWorkspaceMemberLimit);
        _billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(true);

        _workspaceVerifiedDomainRepository.AnyAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = new AcceptInvitationRequest("valid_token");

        // Act
        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, Guid.NewGuid(), "invitee@warptalk.vn");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.TrialWorkspaceMemberLimitReached, result.Error);
        Assert.Equal(InvitationStatus.PENDING.ToString(), invitation.Status);
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

        // B has to actually permit external collaboration for an External invitation to stand —
        // the stored intent is checked against B's live settings, not recomputed into one that
        // happens to pass.
        var workspaceB = new Workspace { Id = workspaceId, AllowExternalCollaboration = true };

        _workspaceInvitationRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspaceB);

        var workspaceA = new Workspace { Id = Guid.NewGuid(), RequireVerifiedDomainForInternal = true };
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

        var workspaceB = new Workspace { Id = workspaceId, RequireVerifiedDomainForInternal = true };

        _workspaceInvitationRepository.GetByTokenHashAsync(tokenHash, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspaceB);
        StubVerifiedDomains(workspaceId, "company.com");

        var workspaceA = new Workspace { Id = Guid.NewGuid(), RequireVerifiedDomainForInternal = true };
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
    public async Task CreateJoinRequestAsync_ShouldReturnPolicyAction_WhenExternalCollaborationDisabled()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = "user@gmail.com";
        var workspace = new Workspace
        {
            Id = workspaceId,
            Slug = "locked-workspace",
            IsActive = true,
            RequireVerifiedDomainForInternal = true,
            AllowExternalCollaboration = false
        };

        _workspaceRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(workspace);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        StubRoleId("Member", roleId);
        _workspaceInvitationRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceInvitation, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns((WorkspaceInvitation?)null);
        _workspaceVerifiedDomainRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var result = await _workspaceInvitationService.CreateJoinRequestAsync(
            new CreateJoinRequestCommand(null, workspace.Slug), userId, email);

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipType.External.ToString(), result.Value!.MembershipType);
        Assert.Empty(result.Value.AllowedFinalMembershipTypes!);
        Assert.True(result.Value.RequiresPolicyAction);
        Assert.Contains(JoinRequestSuggestedActions.EnableExternalCollaboration, result.Value.SuggestedActions!);
        Assert.Contains(JoinRequestSuggestedActions.AddVerifiedDomain, result.Value.SuggestedActions!);
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
                string.Equals(member.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                member.MembershipType == MembershipType.External.ToString()),
            Arg.Any<CancellationToken>());
        await _emailComposer.Received(1).SendJoinRequestApprovedEmailAsync(invitation, workspace, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// WT-416 — a member who left, then asked to come back.
    ///
    /// Leaving is a SOFT delete: the row stays and RemovedAt is stamped. But workspace_members
    /// carries UNIQUE (workspace_id, user_id) with NO `WHERE removed_at IS NULL` predicate, so
    /// the departed row still occupies the slot. The lookup here filtered on RemovedAt == null,
    /// which made that row invisible, and AddAsync then inserted a second row for the same pair:
    /// unique violation, caught by the catch-all, surfaced as 500 "An unexpected error
    /// occurred". Three members of one production workspace were stuck outside it.
    /// </summary>
    [Fact]
    public async Task ApproveJoinRequestAsync_ShouldRevive_WhenRequesterPreviouslyLeftTheWorkspace()
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
        var departed = new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = requesterId,
            RoleId = Guid.NewGuid(),
            Status = "removed",
            MembershipType = MembershipType.Internal.ToString(),
            RemovedAt = DateTime.UtcNow.AddDays(-1),
            RemovedBy = adminId,
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember, departed);
        _authIdentity.GetRoleByIdAsync(adminMember.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminMember.RoleId, Name = "Owner" });
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
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

        Assert.True(result.IsSuccess, "approving a returning member failed — this is the 500 in WT-416");

        // The row is reused, never duplicated: a second insert for this pair is the unique
        // violation itself.
        await _workspaceMemberRepository.DidNotReceive().AddAsync(
            Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
        _workspaceMemberRepository.Received(1).Update(departed);

        // And it comes back as a real membership, not a removed row with a new role.
        Assert.Null(departed.RemovedAt);
        Assert.Null(departed.RemovedBy);
        Assert.Equal(memberRoleId, departed.RoleId);
        Assert.Equal(MembershipType.External.ToString(), departed.MembershipType);
        Assert.Equal("active", departed.Status, ignoreCase: true);
    }

    /// <summary>
    /// The guard that must not move: somebody who is ALREADY a member is still a conflict, not a
    /// revival. Widening the lookup to include removed rows must not make it accept a live one.
    /// </summary>
    [Fact]
    public async Task ApproveJoinRequestAsync_ShouldStillRefuse_WhenRequesterIsAlreadyAnActiveMember()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = "requester@gmail.com",
            RequestedBy = requesterId,
            InvitedBy = requesterId,
            RoleId = Guid.NewGuid(),
            MembershipType = MembershipType.External.ToString(),
            Status = InvitationStatus.REQUESTED.ToString(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        var workspace = new Workspace { Id = workspaceId, Name = "Acme", Slug = "acme", AllowExternalCollaboration = true };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminId, RoleId = Guid.NewGuid() };
        var liveMember = new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = requesterId,
            RoleId = Guid.NewGuid(),
            Status = "active",
            MembershipType = MembershipType.Internal.ToString(),
            RemovedAt = null,
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember, liveMember);
        _authIdentity.GetRoleByIdAsync(adminMember.RoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = adminMember.RoleId, Name = "Owner" });
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(
            workspaceId,
            invitationId,
            adminId,
            new ApproveJoinRequestRequest(MembershipType.External.ToString()));

        Assert.False(result.IsSuccess, "an existing active member was approved a second time");
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(
            Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveJoinRequestAsync_ShouldRejectInternal_WhenRequesterEmailDomainIsNotVerified()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
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
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Acme",
            Slug = "acme",
            AllowExternalCollaboration = true,
            RequireVerifiedDomainForInternal = true
        };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminId, RoleId = adminRoleId };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember);
        StubRoleName(adminRoleId, "Owner");
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceVerifiedDomainRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(
            workspaceId,
            invitationId,
            adminId,
            new ApproveJoinRequestRequest(MembershipType.Internal.ToString()));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(InvitationStatus.REQUESTED.ToString(), invitation.Status);
        await _workspaceMemberRepository.DidNotReceive().AddAsync(Arg.Any<WorkspaceMember>(), Arg.Any<CancellationToken>());
        await _emailComposer.DidNotReceive().SendJoinRequestApprovedEmailAsync(
            Arg.Any<WorkspaceInvitation>(),
            Arg.Any<Workspace>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveJoinRequestAsync_ShouldAllowInternal_WhenRequesterEmailDomainIsVerified()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var invitation = new WorkspaceInvitation
        {
            Id = invitationId,
            WorkspaceId = workspaceId,
            Email = "requester@acme.com",
            RequestedBy = requesterId,
            InvitedBy = requesterId,
            RoleId = memberRoleId,
            MembershipType = MembershipType.Internal.ToString(),
            Status = InvitationStatus.REQUESTED.ToString(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Name = "Acme",
            Slug = "acme",
            AllowExternalCollaboration = false,
            RequireVerifiedDomainForInternal = true
        };
        var adminMember = new WorkspaceMember { WorkspaceId = workspaceId, UserId = adminId, RoleId = adminRoleId };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(adminMember, (WorkspaceMember?)null);
        _workspaceMemberRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), "Workspace", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());
        StubRoleName(adminRoleId, "Owner");
        StubRoleId("Member", memberRoleId);
        _workspaceInvitationRepository.GetByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns(invitation);
        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceVerifiedDomainRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(), "", Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>
            {
                new()
                {
                    WorkspaceId = workspaceId,
                    Domain = "acme.com",
                    Status = "verified",
                    VerifiedAt = DateTime.UtcNow
                }
            });
        _emailComposer.SendJoinRequestApprovedEmailAsync(
            Arg.Any<WorkspaceInvitation>(), Arg.Any<Workspace>(), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "approval-message", null));

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(
            workspaceId,
            invitationId,
            adminId,
            new ApproveJoinRequestRequest(MembershipType.Internal.ToString()));

        Assert.True(result.IsSuccess);
        Assert.Equal(InvitationStatus.ACCEPTED.ToString(), invitation.Status);
        Assert.Equal(MembershipType.Internal.ToString(), invitation.MembershipType);
        await _workspaceMemberRepository.Received(1).AddAsync(
            Arg.Is<WorkspaceMember>(member =>
                member.UserId == requesterId &&
                member.RoleId == memberRoleId &&
                member.MembershipType == MembershipType.Internal.ToString()),
            Arg.Any<CancellationToken>());
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

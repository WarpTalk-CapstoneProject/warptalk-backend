using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class VerifiedDomainServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceVerifiedDomainRepository _verifiedDomainRepo;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly VerifiedDomainService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _ownerRoleId = Guid.NewGuid();
    private readonly Guid _adminRoleId = Guid.NewGuid();
    private readonly Guid _memberRoleId = Guid.NewGuid();

    public VerifiedDomainServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _verifiedDomainRepo = Substitute.For<IWorkspaceVerifiedDomainRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();

        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceVerifiedDomainRepository.Returns(_verifiedDomainRepo);

        _authIdentity.GetRoleByIdAsync(_ownerRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _ownerRoleId, Name = "Owner" });
        _authIdentity.GetRoleByIdAsync(_adminRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _adminRoleId, Name = "Admin" });
        _authIdentity.GetRoleByIdAsync(_memberRoleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _memberRoleId, Name = "Member" });

        _service = new VerifiedDomainService(
            _unitOfWork,
            _authIdentity,
            Substitute.For<ILogger<VerifiedDomainService>>());
    }

    private Workspace SetupWorkspace(bool requireVerifiedDomain = false)
    {
        var workspace = new Workspace
        {
            Id = _workspaceId,
            Name = "Test Enterprise",
            Slug = "test-enterprise",
            RequireVerifiedDomainForInternal = requireVerifiedDomain,
            Settings = $"{{\"RequireVerifiedDomainForInternal\":{requireVerifiedDomain.ToString().ToLowerInvariant()}}}"
        };

        _workspaceRepository.GetByIdAsync(_workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        return workspace;
    }

    private void SetupMember(Guid roleId)
    {
        var member = new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = _userId,
            RoleId = roleId,
            MembershipType = MembershipType.Internal.ToString(),
            JoinedAt = DateTime.UtcNow
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(member);
    }

    /// <summary>
    /// A domain may only be claimed by a caller whose own account email is on it, so
    /// AddDomainAsync now needs the caller's identity resolved.
    /// </summary>
    private void SetupCallerEmail(string email)
    {
        _authIdentity.GetUserByIdAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = _userId, Email = email });
    }

    [Fact]
    public async Task AddDomainAsync_ShouldSucceed_WhenValidCorporateDomain_ByOwner()
    {
        // Arrange
        SetupWorkspace();
        SetupMember(_ownerRoleId);
        SetupCallerEmail("owner@enterprise.com");

        _verifiedDomainRepo.AnyAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        _verifiedDomainRepo.FindAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        // Act
        var result = await _service.AddDomainAsync(_workspaceId, "enterprise.com", _userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("enterprise.com", result.Value.Domain);
        Assert.Equal("verified", result.Value.Status);
        await _verifiedDomainRepo.Received(1).AddAsync(
            Arg.Is<WorkspaceVerifiedDomain>(vd => vd.Domain == "enterprise.com" && vd.WorkspaceId == _workspaceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "DomainValidation")]
    public async Task AddDomainAsync_ShouldFail_WhenPublicDomain()
    {
        // Arrange
        SetupWorkspace();
        SetupMember(_ownerRoleId);

        // Act
        var result = await _service.AddDomainAsync(_workspaceId, "gmail.com", _userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotVerifyPublicDomain, result.Error);
    }

    [Fact]
    [Trait("Category", "DomainValidation")]
    public async Task AddDomainAsync_ShouldFail_WhenClaimingAnotherDomainWithoutConsent()
    {
        // This used to refuse outright: only the caller's own email domain could be claimed.
        // That rule also made a company with several domains — acme.com and acme.vn — unable to
        // register the second one from the same Owner account, which the schema always allowed
        // (the unique index caps a domain at one workspace, not a workspace at one domain).
        //
        // What replaces it is consent, and it is worth being honest about what consent is and
        // is not. It is NOT a barrier against a determined attacker: whoever claims
        // victimcorp.com here can simply agree to the text. What actually keeps a domain from
        // being stolen is the unique index — victimcorp.com can be held by exactly one workspace
        // — plus the business rule that a claim is the claimant's assertion and their
        // responsibility. Consent makes the assertion explicit and recorded, so a wrong claim is
        // attributable rather than deniable.
        // Arrange
        SetupWorkspace();
        SetupMember(_ownerRoleId);
        SetupCallerEmail("owner@attacker.com");

        _verifiedDomainRepo.AnyAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _service.AddDomainAsync(_workspaceId, "victimcorp.com", _userId, consentVersion: null);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.ConsentRequiredForSelfAssertedDomain, result.Error);
        await _verifiedDomainRepo.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<WorkspaceVerifiedDomain>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "DomainValidation")]
    public async Task AddDomainAsync_ShouldRecordSelfAssertedTier_WhenClaimingAnotherDomainWithConsent()
    {
        // The legitimate multi-domain case: one company, several domains, one Owner account.
        // The row records that this claim rests on the Owner's word rather than on their account
        // — the tier and the agreed consent version are written in the same INSERT as the claim,
        // so the evidence cannot end up missing for a claim that succeeded.
        SetupWorkspace();
        SetupMember(_ownerRoleId);
        SetupCallerEmail("owner@acme.com");

        _verifiedDomainRepo.AnyAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        _verifiedDomainRepo.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        WorkspaceVerifiedDomain? added = null;
        await _verifiedDomainRepo.AddAsync(
            Arg.Do<WorkspaceVerifiedDomain>(d => added = d), Arg.Any<CancellationToken>());

        var result = await _service.AddDomainAsync(_workspaceId, "acme.vn", _userId, consentVersion: "2026-08-13");

        Assert.True(result.IsSuccess);
        Assert.NotNull(added);
        Assert.Equal("acme.vn", added!.Domain);
        Assert.Equal(VerifiedDomainVerificationMethods.SelfAsserted, added.VerificationMethod);
        Assert.Equal("2026-08-13", added.VerificationToken);
    }

    [Fact]
    [Trait("Category", "DomainValidation")]
    public async Task AddDomainAsync_ShouldRecordOwnerEmailTier_AndNotAskForConsent_ForTheCallersOwnDomain()
    {
        // Nothing to consent to: the caller's own account is the evidence.
        SetupWorkspace();
        SetupMember(_ownerRoleId);
        SetupCallerEmail("owner@acme.com");

        _verifiedDomainRepo.AnyAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        _verifiedDomainRepo.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        WorkspaceVerifiedDomain? added = null;
        await _verifiedDomainRepo.AddAsync(
            Arg.Do<WorkspaceVerifiedDomain>(d => added = d), Arg.Any<CancellationToken>());

        var result = await _service.AddDomainAsync(_workspaceId, "acme.com", _userId, consentVersion: null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(added);
        Assert.Equal(VerifiedDomainVerificationMethods.OwnerEmail, added!.VerificationMethod);
    }

    [Fact]
    [Trait("Category", "RBAC")]
    public async Task AddDomainAsync_And_RevokeDomainAsync_ShouldFail_WhenCallerIsRegularMember()
    {
        // Arrange
        SetupWorkspace();
        SetupMember(_memberRoleId);

        // Act
        var addResult = await _service.AddDomainAsync(_workspaceId, "company.com", _userId);
        var revokeResult = await _service.RevokeDomainAsync(_workspaceId, Guid.NewGuid(), _userId);

        // Assert
        Assert.False(addResult.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, addResult.Error);

        Assert.False(revokeResult.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, revokeResult.Error);
    }

    [Fact]
    [Trait("Category", "RBAC")]
    public async Task AddDomainAsync_ShouldFail_WhenCallerIsAdmin()
    {
        SetupWorkspace();
        SetupMember(_adminRoleId);

        var result = await _service.AddDomainAsync(_workspaceId, "company.com", _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, result.Error);
        await _verifiedDomainRepo.DidNotReceive().AddAsync(Arg.Any<WorkspaceVerifiedDomain>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "RBAC")]
    public async Task RevokeDomainAsync_ShouldFail_WhenCallerIsAdmin()
    {
        SetupWorkspace();
        SetupMember(_adminRoleId);

        var result = await _service.RevokeDomainAsync(_workspaceId, Guid.NewGuid(), _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.OnlyOwnerCanManageDomains, result.Error);
        _verifiedDomainRepo.DidNotReceive().Update(Arg.Any<WorkspaceVerifiedDomain>());
    }

    [Fact]
    [Trait("Category", "RBAC")]
    public async Task ListDomainsAsync_ShouldSucceed_WhenCallerIsAdmin()
    {
        SetupWorkspace();
        SetupMember(_adminRoleId);
        _verifiedDomainRepo.FindAsync(
                Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain>());

        var result = await _service.ListDomainsAsync(_workspaceId, _userId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    [Trait("Category", "DomainRevocation")]
    public async Task RevokeDomainAsync_ShouldSucceed_AndDropDomainPolicy_WhenRevokingLastDomain()
    {
        // This used to be refused with CannotRevokeLastDomain. That guard contradicted the rule
        // it was guarding: the membership policy is derived from the domain list, so revoking the
        // last domain IS how a workspace returns to assigning membership by hand. Refusing here
        // left no way back — a workspace that ever verified a domain was domain-verified for good.
        //
        // Arrange
        var workspace = SetupWorkspace(requireVerifiedDomain: true);
        SetupMember(_ownerRoleId);

        var domainId = Guid.NewGuid();
        var domain = new WorkspaceVerifiedDomain
        {
            Id = domainId,
            WorkspaceId = _workspaceId,
            Domain = "company.com",
            Status = "verified"
        };

        _verifiedDomainRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(domain);

        // No member is Internal by virtue of this domain, so the guard that DOES remain — the one
        // protecting existing members — has nothing to protect here.
        _workspaceMemberRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());

        // Before revoke the workspace holds this one domain; afterwards it holds none, which is
        // what RecomputeDomainPolicyAsync reads.
        _verifiedDomainRepo.FindAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => domain.RevokedAt == null
                ? new List<WorkspaceVerifiedDomain> { domain }
                : new List<WorkspaceVerifiedDomain>());

        // Act
        var result = await _service.RevokeDomainAsync(_workspaceId, domainId, _userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(domain.RevokedAt);
        Assert.False(workspace.RequireVerifiedDomainForInternal);
    }

    [Fact]
    [Trait("Category", "DomainRevocation")]
    public async Task RevokeDomainAsync_ShouldFail_WhenActiveInternalMembersExist_UsingDomain()
    {
        // Arrange
        SetupWorkspace(requireVerifiedDomain: false);
        SetupMember(_ownerRoleId);

        var domainId = Guid.NewGuid();
        var targetDomain = new WorkspaceVerifiedDomain
        {
            Id = domainId,
            WorkspaceId = _workspaceId,
            Domain = "activecorp.com",
            Status = "verified"
        };

        _verifiedDomainRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(targetDomain);

        var activeInternalMember = new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = Guid.NewGuid(),
            MembershipType = MembershipType.Internal.ToString()
        };

        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember> { activeInternalMember });

        _authIdentity.GetUserByIdAsync(activeInternalMember.UserId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = activeInternalMember.UserId, Email = "employee@activecorp.com" });

        // Act
        var result = await _service.RevokeDomainAsync(_workspaceId, domainId, _userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotRevokeDomainWithActiveMembers, result.Error);
    }

    [Fact]
    [Trait("Category", "DomainRevocation")]
    public async Task RevokeDomainAsync_ShouldSucceed_WhenNoActiveInternalMembers_UsingDomain()
    {
        // Arrange
        SetupWorkspace(requireVerifiedDomain: false);
        SetupMember(_ownerRoleId);

        var domainId = Guid.NewGuid();
        var targetDomain = new WorkspaceVerifiedDomain
        {
            Id = domainId,
            WorkspaceId = _workspaceId,
            Domain = "oldcorp.com",
            Status = "verified"
        };

        _verifiedDomainRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(targetDomain);

        _workspaceMemberRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceMember>());

        // Act
        var result = await _service.RevokeDomainAsync(_workspaceId, domainId, _userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(targetDomain.RevokedAt);
        _verifiedDomainRepo.Received(1).Update(targetDomain);
    }
}

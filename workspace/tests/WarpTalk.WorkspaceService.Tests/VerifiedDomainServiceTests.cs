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
        _unitOfWork.Repository<WorkspaceVerifiedDomain>().Returns(_verifiedDomainRepo);

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

    private void SetupWorkspace(bool requireVerifiedDomain = false)
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

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task AddDomainAsync_ShouldSucceed_WhenValidCorporateDomain_ByOwnerOrAdmin(string roleName)
    {
        // Arrange
        SetupWorkspace();
        var roleId = roleName == "Owner" ? _ownerRoleId : _adminRoleId;
        SetupMember(roleId);

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
        Assert.Equal("enterprise.com", result.Value!.Domain);
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
    [Trait("Category", "DomainRevocation")]
    public async Task RevokeDomainAsync_ShouldFail_WhenLastDomain_And_RequireVerifiedDomainForInternal()
    {
        // Arrange
        SetupWorkspace(requireVerifiedDomain: true);
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

        _verifiedDomainRepo.FindAsync(
            Arg.Any<Expression<Func<WorkspaceVerifiedDomain, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceVerifiedDomain> { domain });

        // Act
        var result = await _service.RevokeDomainAsync(_workspaceId, domainId, _userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.CannotRevokeLastDomain, result.Error);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class DocumentAccessEvaluatorTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceDocumentRepository _documentRepository;
    private readonly IWorkspaceDocumentAccessPolicyRepository _policyRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IConfiguration _configuration;
    private readonly DocumentAccessEvaluator _evaluator;

    public DocumentAccessEvaluatorTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _documentRepository = Substitute.For<IWorkspaceDocumentRepository>();
        _policyRepository = Substitute.For<IWorkspaceDocumentAccessPolicyRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();

        // Setup repository mocks on UnitOfWork
        _unitOfWork.Repository<WorkspaceDocument>().Returns(_documentRepository);
        _unitOfWork.Repository<WorkspaceDocumentAccessPolicy>().Returns(_policyRepository);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_documentRepository);
        _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Returns(_policyRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);

        // Build a real in-memory configuration
        var inMemorySettings = new Dictionary<string, string?>
        {
            { WorkspaceConstants.DefaultExternalGracePeriodHoursKey, "24" }
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        _evaluator = new DocumentAccessEvaluator(
            _unitOfWork,
            _authIdentity,
            _translationRoomClient,
            _configuration,
            Substitute.For<ILogger<DocumentAccessEvaluator>>());
    }

    private void StubRoleName(Guid roleId, string roleName)
    {
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = roleName });
    }

    #region EvaluateAccessAsync Tests

    [Fact]
    public async Task EvaluateAccessAsync_ShouldFail_WhenDocumentNotFound()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceDocument?)null);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.DocumentNotFound, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldFail_WhenUserIsNotWorkspaceMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember?)null);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedNotMember, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldFail_WhenIngestionStatusIsPending_AndUserIsNeitherOwnerNorDocOwner()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString(),
            OwnerId = Guid.NewGuid() // Not current user
        };

        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid(), MembershipType = "Internal" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(member.RoleId, "Member"); // Regular Member (Not Owner/Admin)

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedPendingIngestion, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldProceed_WhenIngestionStatusIsPending_AndUserIsWorkspaceAdmin()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IngestionStatus = WorkspaceDocumentIngestionStatus.pending.ToString(),
            OwnerId = Guid.NewGuid() // Not current user
        };

        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid(), MembershipType = "Internal" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(member.RoleId, "Admin"); // Admin should bypass ingestion pending block
        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>()); // No policies matching

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        // Default action for Internal Member + Non-sensitive document is Success
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldFail_WhenDocumentIsPendingApproval_AndUserIsNeitherOwnerNorDocOwner()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Status = WorkspaceDocumentStatus.pending_approval.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString(),
            OwnerId = Guid.NewGuid(),
            UploadedBy = Guid.NewGuid(),
            ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel
        };

        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = Guid.NewGuid(), MembershipType = "Internal" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        StubRoleName(member.RoleId, "Member");

        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.View);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedDefault, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldDenyAccess_WhenAnyPolicyIsDeny_RegardlessOfAllowPolicies()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed" };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        // Policies: User has an ALLOW policy, but also a membership-type DENY policy
        var policies = new List<WorkspaceDocumentAccessPolicy>
        {
            new() { DocumentId = documentId, SubjectType = WorkspacePolicyConstants.SubjectTypeUser, SubjectId = userId, Permission = "Read", Effect = WorkspacePolicyConstants.EffectAllow },
            new() { DocumentId = documentId, SubjectType = WorkspacePolicyConstants.SubjectTypeMembershipType, SubjectKey = "External", Permission = "Read", Effect = WorkspacePolicyConstants.EffectDeny }
        };

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(policies);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedByPolicy, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowAccess_WhenAllowPolicyExists_AndNoDenyPolicyExists()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed" };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        // Policies: User has an ALLOW policy
        var policies = new List<WorkspaceDocumentAccessPolicy>
        {
            new() { DocumentId = documentId, SubjectType = WorkspacePolicyConstants.SubjectTypeUser, SubjectId = userId, Permission = "Read", Effect = WorkspacePolicyConstants.EffectAllow }
        };

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(policies);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldDenyAccess_WhenSensitiveDocument_AndNoMatchingPolicies()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed", ConfidentialityLevel = "restricted", Status = WorkspaceDocumentStatus.@public.ToString() };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "Internal" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedSensitive, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowAccess_WhenNonSensitiveDocument_AndInternalMember_AndNoMatchingPolicies()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed", ConfidentialityLevel = "general", Status = WorkspaceDocumentStatus.@public.ToString() };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "Internal" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldDenyAccess_WhenNonSensitiveDocument_AndExternalMember_AndNoMeetingException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed", ConfidentialityLevel = "general", Status = WorkspaceDocumentStatus.@public.ToString() };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedDefault, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowExternalUploaderToViewOwnApprovedDocument()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            UploadedBy = userId,
            OwnerId = userId,
            IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString(),
            ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
            Status = WorkspaceDocumentStatus.@public.ToString(),
            SourceType = "UPLOAD"
        };
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = MembershipType.External.ToString()
        };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(member);
        StubRoleName(roleId, "Member");
        _policyRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        var result = await _evaluator.EvaluateAccessAsync(
            userId,
            workspaceId,
            documentId,
            WorkspaceDocumentPermissions.View);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowAccess_WhenExternalMemberAccessesMeetingDocument_WithinGracePeriod()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IngestionStatus = "completed",
            ConfidentialityLevel = "general",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            SourceType = WorkspaceDocumentConstants.SourceTypeMeeting,
            SourceId = meetingId
        };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Mock translation room ended 2 hours ago (within 24-hour grace period)
        var room = new TranslationRoomDto { Id = meetingId, EndedAt = DateTime.UtcNow.AddHours(-2) };
        _translationRoomClient.GetTranslationRoomAsync(meetingId, Arg.Any<CancellationToken>())
            .Returns(room);

        // Mock user as participant
        var participants = new List<TranslationRoomParticipantDto> { new() { Id = userId } };
        _translationRoomClient.GetParticipantsAsync(meetingId, Arg.Any<CancellationToken>())
            .Returns(participants);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldDenyAccess_WhenExternalMemberAccessesMeetingDocument_AfterGracePeriod()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IngestionStatus = "completed",
            ConfidentialityLevel = "general",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            SourceType = WorkspaceDocumentConstants.SourceTypeMeeting,
            SourceId = meetingId
        };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Mock translation room ended 30 hours ago (outside 24-hour grace period)
        var room = new TranslationRoomDto { Id = meetingId, EndedAt = DateTime.UtcNow.AddHours(-30) };
        _translationRoomClient.GetTranslationRoomAsync(meetingId, Arg.Any<CancellationToken>())
            .Returns(room);

        // Mock user as participant
        var participants = new List<TranslationRoomParticipantDto> { new() { Id = userId } };
        _translationRoomClient.GetParticipantsAsync(meetingId, Arg.Any<CancellationToken>())
            .Returns(participants);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "Read");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedDefault, result.Error);
    }

    #endregion

    #region CanManagePoliciesAsync Tests

    [Theory]
    [InlineData("Owner", true)]
    [InlineData("Admin", true)]
    [InlineData("Member", false)]
    public async Task CanManagePoliciesAsync_ShouldEvaluateBasedOnUserRole_WhenNotDocumentOwner(string roleName, bool expectedResult)
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            OwnerId = Guid.NewGuid() // Owned by someone else
        };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, roleName);

        // Act
        var result = await _evaluator.CanManagePoliciesAsync(userId, workspaceId, documentId);

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task CanManagePoliciesAsync_ShouldAllow_WhenUserIsDocumentOwner_EvenIfRegularMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            OwnerId = userId // Owned by current user
        };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        StubRoleName(roleId, "Member");

        // Act
        var result = await _evaluator.CanManagePoliciesAsync(userId, workspaceId, documentId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldDenyDownload_WhenViewIsDenied()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed", Status = WorkspaceDocumentStatus.@public.ToString() };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(roleId, "Member");

        var policies = new List<WorkspaceDocumentAccessPolicy>
        {
            new() { DocumentId = documentId, SubjectType = WorkspacePolicyConstants.SubjectTypeUser, SubjectId = userId, Permission = "view", Effect = WorkspacePolicyConstants.EffectDeny }
        };
        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(policies);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "download");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedByPolicy, result.Error);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowView_WhenDownloadIsAllowed()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument { Id = documentId, WorkspaceId = workspaceId, IngestionStatus = "completed" };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId, MembershipType = "External" };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(roleId, "Member");

        var policies = new List<WorkspaceDocumentAccessPolicy>
        {
            new() { DocumentId = documentId, SubjectType = WorkspacePolicyConstants.SubjectTypeUser, SubjectId = userId, Permission = "download", Effect = WorkspacePolicyConstants.EffectAllow }
        };
        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(policies);

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, "view");

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EvaluateAccessAsync_ShouldAllowDownload_WhenLegacyActiveStatus_AndInternalMember()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            IngestionStatus = "completed",
            Status = "active",
            ConfidentialityLevel = "public_internal"
        };
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = "Internal"
        };

        _documentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        StubRoleName(roleId, "Member");
        _policyRepository.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        // Act
        var result = await _evaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download);

        // Assert
        Assert.True(result.IsSuccess);
    }

    #endregion
}

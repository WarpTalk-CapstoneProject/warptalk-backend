using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Configuration;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using Xunit;

using Microsoft.AspNetCore.Http;
using System.IO;
using System.Text;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceDocumentServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspaceMemberRepository _workspaceMemberRepository;
    private readonly IWorkspaceDocumentRepository _workspaceDocumentRepository;
    private readonly IWorkspaceDocumentAuditRepository _workspaceDocumentAuditRepository;
    private readonly IDocumentAccessEvaluator _accessEvaluator;
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceUrlProvider _urlProvider;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceDocumentStorage _storage;
    private readonly IOptions<ObjectStorageOptions> _storageOptions;
    private readonly WorkspaceDocumentService _documentService;

    public WorkspaceDocumentServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _workspaceRepository = Substitute.For<IWorkspaceRepository>();
        _workspaceMemberRepository = Substitute.For<IWorkspaceMemberRepository>();
        _workspaceDocumentRepository = Substitute.For<IWorkspaceDocumentRepository>();
        _workspaceDocumentAuditRepository = Substitute.For<IWorkspaceDocumentAuditRepository>();
        _accessEvaluator = Substitute.For<IDocumentAccessEvaluator>();
        _eventPublisher = Substitute.For<IWorkspaceDocumentEventPublisher>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _urlProvider = Substitute.For<IWorkspaceUrlProvider>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _storage = Substitute.For<IWorkspaceDocumentStorage>();
        _storage.StorageProviderName.Returns(WorkspaceDocumentConstants.LocalStorageProvider);
        _storageOptions = Options.Create(new ObjectStorageOptions
        {
            Provider = WorkspaceDocumentConstants.LocalStorageProvider
        });

        // Set up mock repository mappings
        _unitOfWork.WorkspaceRepository.Returns(_workspaceRepository);
        _unitOfWork.WorkspaceMemberRepository.Returns(_workspaceMemberRepository);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_workspaceDocumentRepository);
        _unitOfWork.WorkspaceDocumentAuditRepository.Returns(_workspaceDocumentAuditRepository);

        _urlProvider.GetDocumentDownloadUrl(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(x => $"/api/v1/workspaces/{x.ArgAt<Guid>(0)}/documents/{x.ArgAt<Guid>(1)}/download");

        _documentService = new WorkspaceDocumentService(
            _unitOfWork,
            _accessEvaluator,
            _eventPublisher,
            _authIdentity,
            _urlProvider,
            _translationRoomClient,
            _storage,
            Substitute.For<IDocumentTextExtractor>(),
            Substitute.For<ILogger<WorkspaceDocumentService>>()
        );
    }

    private void StubRoleName(Guid roleId, string roleName)
    {
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = roleName });
    }

    [Fact]
    public async Task UploadDocumentAsync_ShouldSetPendingApproval_WhenUserIsMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(memberRoleId, "Member");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns("file.pdf");
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("test content")));
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile);

        // Act
        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(WorkspaceDocumentStatus.pending_approval.ToString(), result.Value.Status);
        Assert.Equal(WorkspaceDocumentIngestionStatus.awaiting_approval.ToString(), result.Value.IngestionStatus);

        await _workspaceDocumentRepository.Received(1).AddAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(
            default, default, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task UploadDocumentAsync_ShouldDeleteStorageBlob_WhenDbSaveFails()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(memberRoleId, "Member");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns("file.pdf");
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("test content")));
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile);

        // The blob write to storage succeeds, but the DB save that should follow it fails —
        // simulating a connection drop after the encrypted file already landed on disk.
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ => throw new InvalidOperationException("DB unavailable"));

        // Act
        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        // Assert
        Assert.False(result.IsSuccess);
        await _storage.Received(1).SaveDocumentContentAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteDocumentContentAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadDocumentAsync_ShouldSetActiveAndPublishEvent_WhenUserIsAdmin()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = adminRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(adminRoleId, "Admin");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns("file.pdf");
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("test content")));
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile);

        // Act
        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(WorkspaceDocumentStatus.@public.ToString(), result.Value.Status);
        Assert.Equal(WorkspaceDocumentIngestionStatus.pending.ToString(), result.Value.IngestionStatus);

        await _workspaceDocumentRepository.Received(1).AddAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishDocumentUploadedAsync(
            Arg.Any<Guid>(), workspaceId, Arg.Any<string>(), "file.pdf", ".pdf", userId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadDocumentAsync_ShouldRejectUnsupportedHtmlFile()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(memberRoleId, "Member");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns("payload.html");
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>")));
        var request = new UploadDocumentApiRequest("Payload", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile);

        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _storage.DidNotReceiveWithAnyArgs().SaveDocumentContentAsync(default!, default!, default);
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(default, default, default!, default!, default!, default, default, default);
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("legacy.xls")]
    public async Task UploadDocumentAsync_ShouldRejectLegacyOfficeFormats(string fileName)
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(memberRoleId, "Member");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns(fileName);
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("legacy")));
        var request = new UploadDocumentApiRequest("Legacy", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile);

        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _storage.DidNotReceiveWithAnyArgs().SaveDocumentContentAsync(default!, default!, default);
    }

    [Fact]
    public async Task UploadDocumentAsync_ShouldStoreImageButSkipAiIngestion()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var memberRoleId = Guid.NewGuid();
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = memberRoleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(memberRoleId, "Member");

        var mockFile = Substitute.For<IFormFile>();
        mockFile.FileName.Returns("chart.png");
        mockFile.Length.Returns(1024);
        mockFile.OpenReadStream().Returns(new MemoryStream([0x89, 0x50, 0x4E, 0x47]));
        var request = new UploadDocumentApiRequest("Chart", "upload", null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, mockFile, IsAiAllowed: true);

        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.IsAiAllowed);
        Assert.Equal(WorkspaceDocumentIngestionStatus.skipped.ToString(), result.Value.IngestionStatus);
        await _storage.Received(1).SaveDocumentContentAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(default, default, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task ApproveDocumentAsync_ShouldApproveAndPublish_WhenAdminApproves()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = adminRoleId };
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Status = WorkspaceDocumentStatus.pending_approval.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.awaiting_approval.ToString(),
            StorageKey = "key",
            FileName = "file.pdf",
            FileExtension = ".pdf",
            UploadedBy = Guid.NewGuid(),
            ConfidentialityLevel = "general",
            IsAiAllowed = true
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(adminRoleId, "Admin");
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        var request = new ApproveDocumentRequest(true);

        // Act
        var result = await _documentService.ApproveDocumentAsync(workspaceId, documentId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceDocumentStatus.@public.ToString(), document.Status);
        Assert.Equal(WorkspaceDocumentIngestionStatus.pending.ToString(), document.IngestionStatus);
        Assert.False(document.AiEligible);

        _workspaceDocumentRepository.Received(1).Update(document);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishDocumentUploadedAsync(
            documentId, workspaceId, "key", "file.pdf", ".pdf", document.UploadedBy.Value, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveDocumentAsync_ShouldRejectAndNotPublish_WhenAdminRejects()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = adminRoleId };
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Status = WorkspaceDocumentStatus.pending_approval.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.awaiting_approval.ToString(),
            UploadedBy = Guid.NewGuid()
        };

        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(adminRoleId, "Admin");
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);

        var request = new ApproveDocumentRequest(false);

        // Act
        var result = await _documentService.ApproveDocumentAsync(workspaceId, documentId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceDocumentStatus.rejected.ToString(), document.Status);
        Assert.False(document.AiEligible);

        _workspaceDocumentRepository.Received(1).Update(document);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(default, default, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task DownloadDocumentAsync_ShouldSucceed_WhenAccessAllowed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Name = "Doc1",
            FileName = "file.pdf",
            FileExtension = ".pdf",
            MimeType = "application/pdf",
            SourceType = "upload",
            IngestionStatus = "completed",
            ConfidentialityLevel = "public_internal",
            RetentionState = "active",
            Status = "active"
        };

        _accessEvaluator.EvaluateAccessAsync(userId, workspaceId, documentId, WorkspaceDocumentPermissions.Download, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _storage.GetDecryptedStreamAsync(document, Arg.Any<CancellationToken>()).Returns(new System.IO.MemoryStream());

        // Act
        var result = await _documentService.DownloadDocumentAsync(workspaceId, documentId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("file.pdf", result.Value.FileName);
        await _workspaceDocumentAuditRepository.Received(1).AddAsync(Arg.Any<WorkspaceDocumentAudit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteDocumentAsync_ShouldSoftDeleteAndPublish_WhenUserIsAuthorized()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId };
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            OwnerId = userId,
            Name = "Doc1",
            FileName = "file.pdf",
            FileExtension = ".pdf",
            MimeType = "application/pdf",
            SourceType = "upload",
            IngestionStatus = "completed",
            ConfidentialityLevel = "public_internal",
            RetentionState = "active",
            Status = "active"
        };

        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        _workspaceMemberRepository.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(member);
        StubRoleName(roleId, "Member");

        // Act
        var result = await _documentService.DeleteDocumentAsync(workspaceId, documentId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(document.DeletedAt);
        Assert.Equal(userId, document.DeletedBy);
        Assert.False(document.AiEligible);

        _workspaceDocumentRepository.Received(1).Update(document);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishDocumentDeletedAsync(documentId, workspaceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessPoliciesAsync_ShouldReturnPaginatedPolicies_WhenAccessAllowed()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, Arg.Any<CancellationToken>()).Returns(true);

        var policies = new List<WorkspaceDocumentAccessPolicy>
        {
            new() { Id = Guid.NewGuid(), DocumentId = documentId, SubjectType = "User", SubjectId = Guid.NewGuid(), Permission = "view", Effect = "ALLOW" },
            new() { Id = Guid.NewGuid(), DocumentId = documentId, SubjectType = "User", SubjectId = Guid.NewGuid(), Permission = "download", Effect = "ALLOW" },
            new() { Id = Guid.NewGuid(), DocumentId = documentId, SubjectType = "User", SubjectId = Guid.NewGuid(), Permission = "view", Effect = "DENY" }
        };

        _unitOfWork.WorkspaceDocumentAccessPolicyRepository.GetPagedAccessPoliciesAsync(
            documentId,
            2,
            2,
            true,
            Arg.Any<CancellationToken>()
        ).Returns((policies.Skip(2).Take(2).ToList(), policies.Count));

        var query = new GetWorkspacesQuery(Page: 2, PageSize: 2);

        // Act
        var result = await _documentService.GetAccessPoliciesAsync(workspaceId, documentId, query, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(3, result.Value.Total);
        Assert.Single(result.Value.Items);
        Assert.Equal(policies[2].Id, result.Value.Items[0].Id);
    }

    [Fact]
    public async Task GetAccessPoliciesAsync_ShouldFail_WhenAccessDenied()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, Arg.Any<CancellationToken>()).Returns(false);

        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10);

        // Act
        var result = await _documentService.GetAccessPoliciesAsync(workspaceId, documentId, query, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    // ---- Confidentiality is a BOUNDARY, not a caption -------------------------------------
    //
    // Every policy check in this service asks one question: WorkspaceDocumentExtensions
    // .IsRestricted(), an equality test against the literal "restricted". The column was free
    // text and both write paths stored whatever arrived, so any other spelling was a document
    // that LOOKED confidential and was read as public by the access evaluator AND by
    // DocumentSecurityGuardrailHelper.HasBasicIndexEligibility — which is what decides whether
    // its text is embedded and answerable by the assistant.

    private (Workspace workspace, WorkspaceMember member, IFormFile file) ArrangeAdminUpload(
        Guid workspaceId, Guid userId, Guid roleId)
    {
        var workspace = new Workspace { Id = workspaceId, IsActive = true };
        var member = new WorkspaceMember { WorkspaceId = workspaceId, UserId = userId, RoleId = roleId };

        _workspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(workspace);
        _workspaceMemberRepository.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);
        StubRoleName(roleId, "Owner");

        var file = Substitute.For<IFormFile>();
        file.FileName.Returns("policy.pdf");
        file.Length.Returns(2048);
        file.OpenReadStream().Returns(new MemoryStream(Encoding.UTF8.GetBytes("body")));
        return (workspace, member, file);
    }

    [Theory]
    [InlineData("confidential")]
    [InlineData("secret")]
    [InlineData("internal")]
    [InlineData("PUBLIC")]
    public async Task UploadDocumentAsync_ShouldRefuse_AConfidentialityLevelNothingReads(string level)
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (_, _, file) = ArrangeAdminUpload(workspaceId, userId, Guid.NewGuid());

        var result = await _documentService.UploadDocumentAsync(
            workspaceId, new UploadDocumentApiRequest("Doc", "upload", null, level, file), userId);

        // Refused, not normalised. Guessing "secret" meant public would be the same failure with
        // better manners; guessing it meant restricted would let a typo lock a document.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _workspaceDocumentRepository.DidNotReceiveWithAnyArgs()
            .AddAsync(default!, default);
    }

    [Theory]
    [InlineData("restricted", WorkspaceDocumentConstants.SensitiveConfidentialityLevel)]
    [InlineData("  restricted  ", WorkspaceDocumentConstants.SensitiveConfidentialityLevel)]
    [InlineData("RESTRICTED", WorkspaceDocumentConstants.SensitiveConfidentialityLevel)]
    [InlineData("Public_Internal", WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel)]
    public async Task UploadDocumentAsync_ShouldStoreTheCanonicalLevel_NotTheCallersSpelling(
        string supplied, string expected)
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (_, _, file) = ArrangeAdminUpload(workspaceId, userId, Guid.NewGuid());

        WorkspaceDocument? stored = null;
        await _workspaceDocumentRepository.AddAsync(
            Arg.Do<WorkspaceDocument>(d => stored = d), Arg.Any<CancellationToken>());

        var result = await _documentService.UploadDocumentAsync(
            workspaceId, new UploadDocumentApiRequest("Doc", "upload", null, supplied, file), userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(stored);
        // "restricted " with one trailing space is an equality miss, and the document would have
        // been indexed and answerable while displaying the word restricted.
        Assert.Equal(expected, stored!.ConfidentialityLevel);
    }

    private WorkspaceDocument ArrangePatchableDocument(
        Guid workspaceId, Guid documentId, Guid userId, string level, string status)
    {
        var document = new WorkspaceDocument
        {
            Id = documentId,
            WorkspaceId = workspaceId,
            Name = "Quarterly plan",
            FileName = "plan.pdf",
            FileExtension = ".pdf",
            StorageKey = $"documents/{workspaceId}/{documentId}.pdf",
            ConfidentialityLevel = level,
            Status = status,
            RetentionState = WorkspaceDocumentConstants.RetentionStateActive,
            IsAiAllowed = true,
            AiEligible = true,
        };

        _accessEvaluator.CanManagePoliciesAsync(userId, workspaceId, documentId, Arg.Any<CancellationToken>())
            .Returns(true);
        _workspaceDocumentRepository.GetByIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    [Fact]
    public async Task PatchDocumentMetadataAsync_ShouldRefuse_AConfidentialityLevelNothingReads()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var document = ArrangePatchableDocument(
            workspaceId, documentId, userId,
            WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
            WorkspaceDocumentStatus.@public.ToString());

        var result = await _documentService.PatchDocumentMetadataAsync(
            workspaceId, documentId, new PatchDocumentRequest(null, "confidential", null), userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        // The label the reader would have trusted must not have moved.
        Assert.Equal(WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, document.ConfidentialityLevel);
    }

    [Fact]
    public async Task PatchDocumentMetadataAsync_ShouldPurgeTheVectors_WhenADocumentBecomesRestricted()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var document = ArrangePatchableDocument(
            workspaceId, documentId, userId,
            WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
            WorkspaceDocumentStatus.@public.ToString());

        var result = await _documentService.PatchDocumentMetadataAsync(
            workspaceId, documentId,
            new PatchDocumentRequest(null, WorkspaceDocumentConstants.SensitiveConfidentialityLevel, null),
            userId);

        Assert.True(result.IsSuccess);
        // THE DEFECT. HasBasicIndexEligibility refuses to index a restricted document, but it is
        // only consulted at upload — so a document indexed while public stayed in the vector store
        // after being marked confidential, and the assistant went on answering out of it.
        await _eventPublisher.Received(1).PublishDocumentDeletedAsync(
            documentId, workspaceId, Arg.Any<CancellationToken>());
        Assert.False(document.AiEligible);
    }

    [Fact]
    public async Task PatchDocumentMetadataAsync_ShouldReindex_WhenARestrictedDocumentIsOpenedUp()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var document = ArrangePatchableDocument(
            workspaceId, documentId, userId,
            WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            WorkspaceDocumentStatus.@public.ToString());
        document.AiEligible = false;

        var result = await _documentService.PatchDocumentMetadataAsync(
            workspaceId, documentId,
            new PatchDocumentRequest(null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, null),
            userId);

        Assert.True(result.IsSuccess);
        await _eventPublisher.Received(1).PublishDocumentUploadedAsync(
            documentId, workspaceId, document.StorageKey, document.FileName, document.FileExtension,
            userId, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchDocumentMetadataAsync_ShouldNotReindex_ADocumentNobodyHasApproved()
    {
        var workspaceId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        ArrangePatchableDocument(
            workspaceId, documentId, userId,
            WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            WorkspaceDocumentStatus.pending_approval.ToString());

        var result = await _documentService.PatchDocumentMetadataAsync(
            workspaceId, documentId,
            new PatchDocumentRequest(null, WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel, null),
            userId);

        Assert.True(result.IsSuccess);
        // Un-restricting is not approval. Indexing here would put a document into the assistant
        // that the workspace has not yet agreed to publish.
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(
            default, default, default!, default!, default!, default, default, default);
    }
}

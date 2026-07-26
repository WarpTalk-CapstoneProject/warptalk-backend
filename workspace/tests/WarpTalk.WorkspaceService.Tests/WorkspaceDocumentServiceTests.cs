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
    private readonly IGenericRepository<WorkspaceDocument> _workspaceDocumentRepository;
    private readonly IGenericRepository<WorkspaceDocumentAudit> _workspaceDocumentAuditRepository;
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
        _workspaceDocumentRepository = Substitute.For<IGenericRepository<WorkspaceDocument>>();
        _workspaceDocumentAuditRepository = Substitute.For<IGenericRepository<WorkspaceDocumentAudit>>();
        _accessEvaluator = Substitute.For<IDocumentAccessEvaluator>();
        _eventPublisher = Substitute.For<IWorkspaceDocumentEventPublisher>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _urlProvider = Substitute.For<IWorkspaceUrlProvider>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _storage = Substitute.For<IWorkspaceDocumentStorage>();
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
            _storageOptions,
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
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, "internal", mockFile);

        // Act
        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
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
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, "internal", mockFile);

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
        var request = new UploadDocumentApiRequest("Doc1", "upload", null, "internal", mockFile);

        // Act
        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        // Assert
        Assert.True(result.IsSuccess);
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
        var request = new UploadDocumentApiRequest("Payload", "upload", null, "internal", mockFile);

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
        var request = new UploadDocumentApiRequest("Legacy", "upload", null, "internal", mockFile);

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
        var request = new UploadDocumentApiRequest("Chart", "upload", null, "internal", mockFile, IsAiAllowed: true);

        var result = await _documentService.UploadDocumentAsync(workspaceId, request, userId);

        Assert.True(result.IsSuccess);
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
            ConfidentialityLevel = "general"
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

        _unitOfWork.WorkspaceDocumentAccessPolicyRepository.FindAsync(
            Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        ).Returns(policies);

        var query = new GetWorkspacesQuery(Page: 2, PageSize: 2);

        // Act
        var result = await _documentService.GetAccessPoliciesAsync(workspaceId, documentId, query, userId);

        // Assert
        Assert.True(result.IsSuccess);
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
}

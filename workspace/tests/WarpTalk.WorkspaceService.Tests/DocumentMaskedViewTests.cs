using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Who gets which version of a PII-restricted document, through the real service and the REAL
/// access evaluator — only storage, the repositories and the identity client are stand-ins.
/// </summary>
/// <remarks>
/// The decision table itself is walked in DocumentContentAccessDecisionTests. These pin that the
/// routes actually ask it: that a member's original download is refused, that the masked copy is
/// what they are handed, and that every way of the masked copy being missing or broken ends in
/// "nothing" rather than in the original.
/// </remarks>
public class DocumentMaskedViewTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceDocumentRepository _documents = Substitute.For<IWorkspaceDocumentRepository>();
    private readonly IWorkspaceMemberRepository _members = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IWorkspaceDocumentAccessPolicyRepository _policies = Substitute.For<IWorkspaceDocumentAccessPolicyRepository>();
    private readonly IWorkspaceDocumentAuditRepository _audits = Substitute.For<IWorkspaceDocumentAuditRepository>();
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly IWorkspaceDocumentStorage _storage = Substitute.For<IWorkspaceDocumentStorage>();
    private readonly IWorkspaceDocumentEventPublisher _eventPublisher = Substitute.For<IWorkspaceDocumentEventPublisher>();
    private readonly IWorkspaceUrlProvider _urlProvider = Substitute.For<IWorkspaceUrlProvider>();
    private readonly WorkspaceDocumentService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _uploaderId = Guid.NewGuid();
    private readonly Guid _roleId = Guid.NewGuid();
    private readonly List<WorkspaceDocumentAccessPolicy> _documentPolicies = new();
    private readonly WorkspaceDocument _document;

    private static readonly byte[] OriginalBytes = Encoding.UTF8.GetBytes("ORIGINAL 0912345678");
    private static readonly byte[] MaskedBytes = Encoding.UTF8.GetBytes("MASKED [PHONE_REDACTED]");

    public DocumentMaskedViewTests()
    {
        _unitOfWork.WorkspaceRepository.Returns(_workspaces);
        _unitOfWork.WorkspaceDocumentRepository.Returns(_documents);
        _unitOfWork.WorkspaceMemberRepository.Returns(_members);
        _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Returns(_policies);
        _unitOfWork.WorkspaceDocumentAuditRepository.Returns(_audits);

        _workspaces.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new Workspace { Id = call.ArgAt<Guid>(0), Name = "Acme", Slug = "acme", Settings = "{}", IsActive = true });

        _document = new WorkspaceDocument
        {
            Id = _documentId,
            WorkspaceId = _workspaceId,
            UploadedBy = _uploaderId,
            OwnerId = _uploaderId,
            Name = "Staff list",
            FileName = "staff-list.docx",
            FileExtension = ".docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SourceType = "upload",
            StorageKey = $"documents/{_workspaceId}/{_documentId}.docx",
            IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString(),
            ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            RetentionState = "active",
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IsAiAllowed = true
        };

        _documents.GetByIdAsync(_documentId, Arg.Any<CancellationToken>()).Returns(_document);
        _documents.FindAsync(Arg.Any<Expression<Func<WorkspaceDocument, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<WorkspaceDocument> { _document });
        _policies.FindAsync(Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<WorkspaceDocumentAccessPolicy>(_documentPolicies));
        _audits.GetLatestApproverUserIdsByWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid?>());

        _storage.GetDecryptedStreamAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(OriginalBytes));
        _urlProvider.GetDocumentDownloadUrl(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns("/original-download-url");

        var evaluator = new DocumentAccessEvaluator(
            _unitOfWork,
            _authIdentity,
            Substitute.For<ITranslationRoomClient>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<DocumentAccessEvaluator>>());

        _service = new WorkspaceDocumentService(
            _unitOfWork,
            evaluator,
            _eventPublisher,
            _authIdentity,
            _urlProvider,
            Substitute.For<ITranslationRoomClient>(),
            _storage,
            Substitute.For<IDocumentTextExtractor>(),
            Substitute.For<IKnowledgeChunkWriter>(),
            Substitute.For<ILogger<WorkspaceDocumentService>>());

        AsRole("Member");
    }

    // ------------------------------------------------------------ arrangement

    private void AsRole(string roleName, string membershipType = "Internal")
    {
        _members.FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = _userId, RoleId = _roleId, MembershipType = membershipType });
        _authIdentity.GetRoleByIdAsync(_roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _roleId, Name = roleName });
    }

    private void MaskedCopyExists()
    {
        _storage.MaskedFileExistsAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>()).Returns(true);
        _storage.GetMaskedFileStreamAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(MaskedBytes));
    }

    private void LastScan(bool pii, bool dlp, string? maskedVersion = null, DateTime? at = null)
    {
        _audits.GetLatestActionAsync(_documentId, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit
            {
                Id = Guid.NewGuid(),
                DocumentId = _documentId,
                Action = WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted,
                ActionAt = at ?? DateTime.UtcNow.AddHours(-1),
                Metadata = JsonSerializer.Serialize(new { ViolationFound = pii || dlp, PiiDetected = pii, DlpDetected = dlp, MaskedVersion = maskedVersion })
            });
    }

    private void Policy(string effect, string permission)
    {
        _documentPolicies.Add(new WorkspaceDocumentAccessPolicy
        {
            Id = Guid.NewGuid(),
            DocumentId = _documentId,
            WorkspaceId = _workspaceId,
            SubjectType = WorkspacePolicyConstants.SubjectTypeUser,
            SubjectId = _userId,
            Effect = effect,
            Permission = permission
        });
    }

    private static string Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private Task<Result<WorkspaceDocumentDto>> Detail() => _service.GetDocumentByIdAsync(_workspaceId, _documentId, _userId);
    private Task<Result<DocumentDownloadStreamDto>> DownloadOriginal() => _service.DownloadDocumentAsync(_workspaceId, _documentId, _userId);
    private Task<Result<DocumentDownloadStreamDto>> DownloadMasked() => _service.DownloadMaskedDocumentAsync(_workspaceId, _documentId, _userId);

    private async Task AssertOriginalNeverReadAsync() =>
        await _storage.DidNotReceiveWithAnyArgs().GetDecryptedStreamAsync(default!, default);

    // ------------------------------------------------------------------ member

    [Fact]
    public async Task Member_IsToldTheContentIsMasked_AndGivenNoOriginalUrl()
    {
        MaskedCopyExists();
        LastScan(pii: true, dlp: false, WorkspaceDocumentMaskedVersionStatuses.Available);

        var result = await Detail();

        Assert.True(result.IsSuccess);
        Assert.Equal("masked", result.Value!.ContentAccess);
        Assert.Null(result.Value.DownloadUrl);
        Assert.True(result.Value.MaskedVersionAvailable);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Available, result.Value.MaskedVersionStatus);
        Assert.False(result.Value.CanRescanMaskedVersion);
    }

    [Fact]
    public async Task Member_OriginalDownload_Is403_AsItAlwaysWas()
    {
        MaskedCopyExists();

        var result = await DownloadOriginal();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task Member_MaskedDownload_ServesTheMaskedFile_InTheOriginalFormat()
    {
        MaskedCopyExists();

        var result = await DownloadMasked();

        Assert.True(result.IsSuccess);
        Assert.Equal("Staff list (masked).docx", result.Value!.FileName);
        Assert.Equal(_document.MimeType, result.Value.ContentType);
        Assert.Equal("MASKED [PHONE_REDACTED]", Read(result.Value.Stream));
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task Member_HoldingAnAllowPolicy_IsStillOnTheMaskedCopy_AndRefusedTheOriginal()
    {
        MaskedCopyExists();
        Policy(WorkspacePolicyConstants.EffectAllow, WorkspaceDocumentPermissions.Download);

        var detail = await Detail();
        var original = await DownloadOriginal();
        var masked = await DownloadMasked();
        var text = await _service.GetExtractedTextAsync(_workspaceId, _documentId, _userId);

        Assert.Equal("masked", detail.Value!.ContentAccess);
        Assert.Null(detail.Value.DownloadUrl);

        Assert.False(original.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, original.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedOriginalContent, original.Error);

        Assert.True(masked.IsSuccess);

        Assert.False(text.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, text.ErrorCode);
        await AssertOriginalNeverReadAsync();
        await _storage.DidNotReceiveWithAnyArgs().GetExtractedTextAsync(default!, default);
    }

    [Fact]
    public async Task Member_PiiRestricted_WithNoMaskedCopyYet_OpensThePage_AndGetsNoContent()
    {
        LastScan(pii: true, dlp: false);

        var detail = await Detail();
        var original = await DownloadOriginal();
        var masked = await DownloadMasked();

        Assert.True(detail.IsSuccess);
        Assert.Equal("none", detail.Value!.ContentAccess);
        Assert.Null(detail.Value.DownloadUrl);
        Assert.False(detail.Value.MaskedVersionAvailable);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.NotGenerated, detail.Value.MaskedVersionStatus);

        Assert.Equal(ErrorCodes.Forbidden, original.ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, masked.ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task Member_HoldingAnAllowPolicy_PiiRestricted_WithNoMaskedCopy_GetsNothing()
    {
        LastScan(pii: true, dlp: false, WorkspaceDocumentMaskedVersionStatuses.VerificationFailed);
        Policy(WorkspacePolicyConstants.EffectAllow, WorkspaceDocumentPermissions.Download);

        var detail = await Detail();
        var original = await DownloadOriginal();

        Assert.Equal("none", detail.Value!.ContentAccess);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.VerificationFailed, detail.Value.MaskedVersionStatus);
        Assert.Equal(ErrorCodes.Forbidden, original.ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Theory]
    [InlineData(false, true)] // a banned keyword
    [InlineData(true, true)]  // personal details AND a banned keyword
    public async Task Member_DlpRestricted_StaysBlockedEntirely(bool pii, bool dlp)
    {
        LastScan(pii, dlp, WorkspaceDocumentMaskedVersionStatuses.DlpBlocked);

        var detail = await Detail();

        Assert.False(detail.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, detail.ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadOriginal()).ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadMasked()).ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task Member_RestrictedWithNoScanOnRecord_StaysBlockedEntirely()
    {
        var detail = await Detail();

        Assert.False(detail.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, detail.ErrorCode);
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedSensitive, detail.Error);
    }

    [Fact]
    public async Task Member_HoldingAnAllowPolicy_OnADocumentRestrictedForAnotherReason_KeepsTheOriginal_AsToday()
    {
        LastScan(pii: false, dlp: true);
        Policy(WorkspacePolicyConstants.EffectAllow, WorkspaceDocumentPermissions.Download);

        var detail = await Detail();
        var original = await DownloadOriginal();

        Assert.Equal("original", detail.Value!.ContentAccess);
        Assert.True(original.IsSuccess);
        Assert.Equal("ORIGINAL 0912345678", Read(original.Value!.Stream));
    }

    [Fact]
    public async Task Member_DeniedByPolicy_GetsNothing_EvenWithAMaskedCopy()
    {
        MaskedCopyExists();
        Policy(WorkspacePolicyConstants.EffectDeny, WorkspaceDocumentPermissions.View);

        Assert.Equal(ErrorCodes.Forbidden, (await Detail()).ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadMasked()).ErrorCode);
        await _storage.DidNotReceiveWithAnyArgs().GetMaskedFileStreamAsync(default!, default);
    }

    [Fact]
    public async Task ExternalMember_GetsNoMaskedCopy_OfADocumentTheyCouldNotSeeAnyway()
    {
        AsRole("Member", membershipType: "External");
        MaskedCopyExists();

        Assert.Equal(ErrorCodes.Forbidden, (await Detail()).ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadMasked()).ErrorCode);
    }

    // ------------------------------------------------------------ fail closed

    [Fact]
    public async Task AMaskedCopyThatCannotBeRead_IsAnError_NeverTheOriginal()
    {
        MaskedCopyExists();
        _storage.GetMaskedFileStreamAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("HMAC mismatch"));

        var result = await DownloadMasked();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task AMaskedCopyThatVanished_Is404_NeverTheOriginal()
    {
        _storage.MaskedFileExistsAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>()).Returns(true);
        _storage.GetMaskedFileStreamAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .Returns((Stream?)null);

        var result = await DownloadMasked();

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    [Fact]
    public async Task AStoreThatCannotSayWhetherACopyExists_MeansNoCopy_ForAMember()
    {
        LastScan(pii: true, dlp: false, WorkspaceDocumentMaskedVersionStatuses.Available);
        _storage.MaskedFileExistsAsync(Arg.Any<WorkspaceDocument>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("store unreachable"));

        var detail = await Detail();

        Assert.Equal("none", detail.Value!.ContentAccess);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadOriginal()).ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, (await DownloadMasked()).ErrorCode);
        await AssertOriginalNeverReadAsync();
    }

    // -------------------------------------------- Owner/Admin and the uploader

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task OwnerAndAdmin_GetTheOriginal_TheMaskedCopyToo_AndMayRescan(string role)
    {
        AsRole(role);
        MaskedCopyExists();
        LastScan(pii: true, dlp: false, WorkspaceDocumentMaskedVersionStatuses.Available);

        var detail = await Detail();
        var original = await DownloadOriginal();
        var masked = await DownloadMasked();

        Assert.Equal("original", detail.Value!.ContentAccess);
        Assert.Equal("/original-download-url", detail.Value.DownloadUrl);
        Assert.True(detail.Value.MaskedVersionAvailable);
        Assert.True(detail.Value.CanRescanMaskedVersion);
        Assert.Equal("ORIGINAL 0912345678", Read(original.Value!.Stream));
        Assert.Equal("MASKED [PHONE_REDACTED]", Read(masked.Value!.Stream));
    }

    [Fact]
    public async Task Owner_WithNoMaskedCopy_SeesWhy_AndTheMaskedRouteIs404()
    {
        AsRole("Owner");
        LastScan(pii: true, dlp: false, WorkspaceDocumentMaskedVersionStatuses.UnsupportedContent);

        var detail = await Detail();

        Assert.Equal("original", detail.Value!.ContentAccess);
        Assert.False(detail.Value.MaskedVersionAvailable);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.UnsupportedContent, detail.Value.MaskedVersionStatus);
        Assert.Equal(ErrorCodes.NotFound, (await DownloadMasked()).ErrorCode);
    }

    [Fact]
    public async Task TheUploader_AnOrdinaryMember_GetsTheOriginal_ButMayNotRescan()
    {
        _document.UploadedBy = _userId;
        _document.OwnerId = _userId;
        MaskedCopyExists();

        var detail = await Detail();
        var original = await DownloadOriginal();

        Assert.Equal("original", detail.Value!.ContentAccess);
        Assert.False(detail.Value.CanRescanMaskedVersion);
        Assert.True(original.IsSuccess);
    }

    // ---------------------------------------------- not restricted: as today

    [Fact]
    public async Task ADocumentThatIsNotRestricted_BehavesExactlyAsBefore()
    {
        _document.ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;
        _document.IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString();

        var detail = await Detail();
        var original = await DownloadOriginal();
        var masked = await DownloadMasked();

        Assert.Equal("original", detail.Value!.ContentAccess);
        Assert.Equal("/original-download-url", detail.Value.DownloadUrl);
        Assert.False(detail.Value.MaskedVersionAvailable);
        Assert.Null(detail.Value.MaskedVersionStatus);
        Assert.True(original.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, masked.ErrorCode);

        // None of the masked-copy lookups run for a document that is not restricted.
        await _storage.DidNotReceiveWithAnyArgs().MaskedFileExistsAsync(default!, default);
        await _audits.DidNotReceive().GetLatestActionAsync(
            _documentId, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------- list

    [Fact]
    public async Task List_ShowsAMemberTheDocument_OnlyOnceItHasAMaskedCopy()
    {
        LastScan(pii: true, dlp: false);

        var before = await _service.ListDocumentsAsync(_workspaceId, new GetDocumentsQuery(), _userId);
        Assert.Empty(before.Value!.Items);

        MaskedCopyExists();
        var after = await _service.ListDocumentsAsync(_workspaceId, new GetDocumentsQuery(), _userId);

        var row = Assert.Single(after.Value!.Items);
        Assert.Equal("masked", row.ContentAccess);
        Assert.Null(row.DownloadUrl);
    }

    [Fact]
    public async Task List_ShowsAnOwnerTheDocument_AsOriginal()
    {
        AsRole("Owner");

        var result = await _service.ListDocumentsAsync(_workspaceId, new GetDocumentsQuery(), _userId);

        var row = Assert.Single(result.Value!.Items);
        Assert.Equal("original", row.ContentAccess);
        Assert.Equal("/original-download-url", row.DownloadUrl);
    }

    // ------------------------------------------------------------------ rescan

    [Fact]
    public async Task Rescan_ByAnOwner_RecordsTheRequest_AndPublishesTheSameEventAnUploadDoes()
    {
        AsRole("Owner");
        LastScan(pii: true, dlp: false);
        WorkspaceDocumentAudit? requested = null;
        await _audits.AddAsync(Arg.Do<WorkspaceDocumentAudit>(a => requested = a), Arg.Any<CancellationToken>());

        var result = await _service.RescanMaskedVersionAsync(_workspaceId, _documentId, _userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(requested);
        Assert.Equal(WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, requested!.Action);
        Assert.Equal(_userId, requested.ActorId);
        await _eventPublisher.Received(1).PublishDocumentUploadedAsync(
            _documentId, _workspaceId, _document.StorageKey, _document.FileName, _document.FileExtension,
            _userId, _document.ConfidentialityLevel, Arg.Any<CancellationToken>());
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rescan_AlreadyPending_DoesNotAskTwice_AndReportsPending()
    {
        AsRole("Owner");
        LastScan(pii: true, dlp: false, at: DateTime.UtcNow.AddHours(-2));
        _audits.GetLatestActionAsync(_documentId, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit { DocumentId = _documentId, ActionAt = DateTime.UtcNow.AddMinutes(-1) });

        var result = await _service.RescanMaskedVersionAsync(_workspaceId, _documentId, _userId);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Pending, result.Value!.MaskedVersionStatus);
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(default, default, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Rescan_ThatFailed_IsReportedAsAnError_UntilTheNextOne()
    {
        AsRole("Owner");
        LastScan(pii: true, dlp: false, at: DateTime.UtcNow.AddHours(-2));
        _audits.GetLatestActionAsync(_documentId, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit { DocumentId = _documentId, ActionAt = DateTime.UtcNow.AddMinutes(-10) });
        _audits.GetLatestActionAsync(_documentId, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanFailed, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceDocumentAudit { DocumentId = _documentId, ActionAt = DateTime.UtcNow.AddMinutes(-9) });

        var detail = await Detail();

        Assert.Equal(WorkspaceDocumentMaskedVersionStatuses.Error, detail.Value!.MaskedVersionStatus);
    }

    [Fact]
    public async Task Rescan_ByAMember_OrTheUploader_IsForbidden()
    {
        _document.UploadedBy = _userId;
        _document.OwnerId = _userId;

        var result = await _service.RescanMaskedVersionAsync(_workspaceId, _documentId, _userId);

        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishDocumentUploadedAsync(default, default, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Rescan_OfADocumentThatIsNotRestricted_IsRefused()
    {
        AsRole("Owner");
        _document.ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;

        var result = await _service.RescanMaskedVersionAsync(_workspaceId, _documentId, _userId);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }
}

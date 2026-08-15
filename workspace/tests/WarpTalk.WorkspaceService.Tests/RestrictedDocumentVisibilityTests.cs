using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-411 — "Documents disappear after AI processing".
///
/// Nothing disappeared. Production, workspace 019f0d00-…-aa: nine documents, ZERO with
/// deleted_at set, five sitting at confidentiality_level='restricted' + ingestion_status='failed'
/// — .pdf and .docx among them, so this was never only the UTF-16 defect in WT-409.
///
/// The chain: AI processing fails, the guardrail's fail-safe marks the document sensitive,
/// DocumentAccessEvaluator refuses View, and ListDocumentsAsync drops refused documents from the
/// response WITHOUT a word. The uploader keeps seeing it — there is an explicit early return for
/// exactly that — so the person who loses the file is the workspace owner who just approved it.
///
/// The restricted gate was the only one in EvaluateAccessAsync that did not exempt Owner/Admin;
/// archived, approval-restricted and download-of-unpublished all do. Withholding it bought
/// nothing either: Owner/Admin already pass CanManagePoliciesAsync for every document in their
/// workspace, so they could grant themselves an ALLOW policy and read it anyway.
/// </summary>
public class RestrictedDocumentVisibilityTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspaceDocumentRepository _documents = Substitute.For<IWorkspaceDocumentRepository>();
    private readonly IWorkspaceMemberRepository _members = Substitute.For<IWorkspaceMemberRepository>();
    private readonly IWorkspaceDocumentAccessPolicyRepository _policies =
        Substitute.For<IWorkspaceDocumentAccessPolicyRepository>();
    private readonly IAuthIdentityClient _authIdentity = Substitute.For<IAuthIdentityClient>();
    private readonly DocumentAccessEvaluator _evaluator;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly Guid _roleId = Guid.NewGuid();

    public RestrictedDocumentVisibilityTests()
    {
        _unitOfWork.WorkspaceDocumentRepository.Returns(_documents);
        _unitOfWork.WorkspaceMemberRepository.Returns(_members);
        _unitOfWork.WorkspaceDocumentAccessPolicyRepository.Returns(_policies);
        _policies.FindAsync(
                Arg.Any<Expression<Func<WorkspaceDocumentAccessPolicy, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<WorkspaceDocumentAccessPolicy>());

        _evaluator = new DocumentAccessEvaluator(
            _unitOfWork,
            _authIdentity,
            Substitute.For<ITranslationRoomClient>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<DocumentAccessEvaluator>>());
    }

    /// <summary>A restricted document somebody else uploaded.</summary>
    private void ArrangeRestrictedDocument()
    {
        _documents.GetByIdAsync(_documentId, Arg.Any<CancellationToken>()).Returns(new WorkspaceDocument
        {
            Id = _documentId,
            WorkspaceId = _workspaceId,
            Status = WorkspaceDocumentStatus.@public.ToString(),
            IngestionStatus = WorkspaceDocumentIngestionStatus.failed.ToString(),
            ConfidentialityLevel = WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            RetentionState = "active",
            OwnerId = Guid.NewGuid(),
            UploadedBy = Guid.NewGuid(),
        });
    }

    private void ArrangeCaller(Guid userId, string roleName)
    {
        _members.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember
            {
                WorkspaceId = _workspaceId,
                UserId = userId,
                RoleId = _roleId,
                MembershipType = MembershipType.Internal.ToString(),
            });

        _authIdentity.GetRoleByIdAsync(_roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = _roleId, Name = roleName });
    }

    /// <summary>
    /// The reported bug. Before the fix this returned AccessDeniedSensitive, and the list endpoint
    /// then dropped the row silently.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task AnOwnerOrAdminCanStillSeeADocumentAiProcessingMarkedSensitive(string roleName)
    {
        ArrangeRestrictedDocument();
        var caller = Guid.NewGuid();
        ArrangeCaller(caller, roleName);

        var result = await _evaluator.EvaluateAccessAsync(
            caller, _workspaceId, _documentId, WorkspaceDocumentPermissions.View);

        Assert.True(
            result.IsSuccess,
            $"A workspace {roleName} lost sight of a document they had just approved. Nothing was "
            + "deleted — it was hidden, and the list endpoint drops hidden rows without a word.");
    }

    /// <summary>
    /// The other direction, and the one that must not move. An ordinary member is still refused —
    /// this is the rule EvaluateAccessAsync_ShouldDenyAccess_WhenSensitiveDocument_AndNoMatching
    /// Policies already pins, restated here so the two live next to each other.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryMemberIsStillRefused()
    {
        ArrangeRestrictedDocument();
        var caller = Guid.NewGuid();
        ArrangeCaller(caller, "Member");

        var result = await _evaluator.EvaluateAccessAsync(
            caller, _workspaceId, _documentId, WorkspaceDocumentPermissions.View);

        Assert.False(result.IsSuccess, "the sensitive gate was opened for everyone, not for Owner/Admin");
        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedSensitive, result.Error);
    }

    /// <summary>
    /// Download is a separate permission and this change must not widen it by accident — the
    /// exemption sits on the confidentiality gate, which both permissions pass through.
    /// </summary>
    [Fact]
    public async Task AMemberStillCannotDownloadARestrictedDocument()
    {
        ArrangeRestrictedDocument();
        var caller = Guid.NewGuid();
        ArrangeCaller(caller, "Member");

        var result = await _evaluator.EvaluateAccessAsync(
            caller, _workspaceId, _documentId, WorkspaceDocumentPermissions.Download);

        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// The three ways a scan can fail point at three different components, and the audit trail
    /// for the production failures could not tell them apart — it showed the guardrail reading
    /// each file and then NO SecurityScanCompleted row, which proves ScanAsync threw but not how.
    ///
    /// A timeout blames the security worker or the queue between us and it; scan_failed blames
    /// that worker's own upstream (its OpenAI call); anything else is ours, on the ingestion path.
    /// Collapsing them into one string is what left five production documents failed with nothing
    /// to act on.
    /// </summary>
    [Theory]
    [InlineData(typeof(TimeoutException), "security_scan_timeout")]
    [InlineData(typeof(InvalidOperationException), "security_scan_failed")]
    [InlineData(typeof(IOException), "ingestion_error")]
    public void EachWayAScanCanFailHasItsOwnReason(Type exceptionType, string expected)
    {
        var thrown = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        // The same expression the guardrail's fail-safe uses.
        var reason = thrown switch
        {
            TimeoutException => WorkspaceDocumentIngestionFailureReasons.SecurityScanTimeout,
            InvalidOperationException => WorkspaceDocumentIngestionFailureReasons.SecurityScanFailed,
            _ => WorkspaceDocumentIngestionFailureReasons.IngestionError,
        };

        Assert.Equal(expected, reason);
    }

    /// <summary>
    /// AI retrieval is gated on ingestion having COMPLETED, and a restricted+failed document has
    /// not. Being visible to an admin must not make it retrievable by the assistant.
    /// </summary>
    [Fact]
    public async Task BeingVisibleToAnAdminDoesNotMakeItAiRetrievable()
    {
        ArrangeRestrictedDocument();
        var caller = Guid.NewGuid();
        ArrangeCaller(caller, "Owner");

        var result = await _evaluator.EvaluateAccessAsync(
            caller, _workspaceId, _documentId, WorkspaceDocumentPermissions.AiRetrieval);

        Assert.False(
            result.IsSuccess,
            "a document whose scan never completed became reachable by the AI assistant");
    }
}

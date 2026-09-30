using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// What `private` means to the evaluator: the document was taken back from the workspace, so
/// the default audience is gone and only a named person, the uploader or an Owner/Admin may read.
/// </summary>
/// <remarks>
/// Before the status existed, a published document could not be withdrawn at all — the detail
/// page showed PUBLIC with no way back. These tests pin the read side of the revoke: the status
/// alone must cut the reads, not a UI that happens to hide a button.
/// </remarks>
public class PrivateDocumentAccessTests
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _uploaderId = Guid.NewGuid();
    private readonly DocumentAccessEvaluator _evaluator;

    public PrivateDocumentAccessTests()
    {
        _evaluator = new DocumentAccessEvaluator(
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IAuthIdentityClient>(),
            Substitute.For<ITranslationRoomClient>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<DocumentAccessEvaluator>>());
    }

    private WorkspaceDocument Document(WorkspaceDocumentStatus status) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UploadedBy = _uploaderId,
        OwnerId = _uploaderId,
        Status = status.ToString(),
        IngestionStatus = WorkspaceDocumentIngestionStatus.completed.ToString(),
        RetentionState = WorkspaceDocumentConstants.RetentionStateActive,
        ConfidentialityLevel = WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel,
        IsAiAllowed = true,
        AiEligible = true,
        LastIndexedAt = DateTime.UtcNow,
    };

    private WorkspaceMember Member(Guid userId, MembershipType type = MembershipType.Internal) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = userId,
        MembershipType = type.ToString(),
        Status = "active",
    };

    private static WorkspaceDocumentAccessPolicy Policy(
        WorkspaceDocument document, string subjectType, string permission, string effect,
        Guid? subjectId = null, string? subjectKey = null) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = document.Id,
        WorkspaceId = document.WorkspaceId,
        SubjectType = subjectType,
        SubjectId = subjectId,
        SubjectKey = subjectKey,
        Permission = permission,
        Effect = effect,
    };

    private Task<WarpTalk.Shared.Result> Evaluate(
        WorkspaceDocument document,
        WorkspaceMember member,
        string permission,
        WorkspaceMemberRole role = WorkspaceMemberRole.Member,
        params WorkspaceDocumentAccessPolicy[] policies)
        => _evaluator.EvaluateAccessAsync(
            member.UserId, _workspaceId, document, permission, member, role.ToRoleName(), policies);

    [Fact]
    public async Task APublicDocument_IsReadableByAnInternalMember_ByDefault()
    {
        // The baseline the revoke has to move off. Without it, a denial below could be any gate.
        var result = await Evaluate(
            Document(WorkspaceDocumentStatus.@public), Member(Guid.NewGuid()), WorkspaceDocumentPermissions.View);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(WorkspaceDocumentPermissions.View)]
    [InlineData(WorkspaceDocumentPermissions.Download)]
    [InlineData(WorkspaceDocumentPermissions.AiRetrieval)]
    public async Task APrivateDocument_RefusesAnInternalMember_WhoIsNotNamed(string permission)
    {
        var result = await Evaluate(Document(WorkspaceDocumentStatus.@private), Member(Guid.NewGuid()), permission);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task APrivateDocument_SaysWhyItRefused()
    {
        var result = await Evaluate(
            Document(WorkspaceDocumentStatus.@private), Member(Guid.NewGuid()), WorkspaceDocumentPermissions.View);

        Assert.Equal(WorkspaceConstants.Errors.AccessDeniedPrivate, result.Error);
    }

    [Fact]
    public async Task APrivateDocument_IgnoresAnAllowOnTheMemberRole()
    {
        // "Every member may view this" is the very audience private withdraws. Honouring it would
        // leave the document exactly as shared as it was before the revoke.
        var document = Document(WorkspaceDocumentStatus.@private);
        var result = await Evaluate(
            document, Member(Guid.NewGuid()), WorkspaceDocumentPermissions.View, WorkspaceMemberRole.Member,
            Policy(document, WorkspacePolicyConstants.SubjectTypeRole, "view", WorkspacePolicyConstants.EffectAllow,
                subjectKey: WorkspaceMemberRole.Member.ToRoleName()));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task APrivateDocument_IgnoresTheExternalUsersSwitch()
    {
        var document = Document(WorkspaceDocumentStatus.@private);
        var result = await Evaluate(
            document, Member(Guid.NewGuid(), MembershipType.External), WorkspaceDocumentPermissions.View,
            WorkspaceMemberRole.Member,
            Policy(document, WorkspacePolicyConstants.SubjectTypeMembershipType, "view", WorkspacePolicyConstants.EffectAllow,
                subjectKey: MembershipType.External.ToString()));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task APrivateDocument_StillHonoursTheExternalSwitch_OnceItIsPublicAgain()
    {
        // The row is kept, not deleted, and the panel promises it applies again — pin that.
        var document = Document(WorkspaceDocumentStatus.@public);
        var result = await Evaluate(
            document, Member(Guid.NewGuid(), MembershipType.External), WorkspaceDocumentPermissions.View,
            WorkspaceMemberRole.Member,
            Policy(document, WorkspacePolicyConstants.SubjectTypeMembershipType, "view", WorkspacePolicyConstants.EffectAllow,
                subjectKey: MembershipType.External.ToString()));

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(WorkspaceDocumentPermissions.View)]
    [InlineData(WorkspaceDocumentPermissions.Download)]
    public async Task APrivateDocument_IsReadableByAPersonItNames(string permission)
    {
        // Download too: the detail page's preview IS the download route, so "may view" that
        // refused the download would render a document they are entitled to as a broken frame.
        var named = Member(Guid.NewGuid());
        var document = Document(WorkspaceDocumentStatus.@private);
        var result = await Evaluate(
            document, named, permission, WorkspaceMemberRole.Member,
            Policy(document, WorkspacePolicyConstants.SubjectTypeUser, "view", WorkspacePolicyConstants.EffectAllow,
                subjectId: named.UserId));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task APrivateDocument_IsNeverRetrievableByTheAssistant_EvenForItsNamedPeople()
    {
        var named = Member(Guid.NewGuid());
        var document = Document(WorkspaceDocumentStatus.@private);
        var result = await Evaluate(
            document, named, WorkspaceDocumentPermissions.AiRetrieval, WorkspaceMemberRole.Member,
            Policy(document, WorkspacePolicyConstants.SubjectTypeUser, "ai_retrieval", WorkspacePolicyConstants.EffectAllow,
                subjectId: named.UserId));

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(WorkspaceMemberRole.Owner)]
    [InlineData(WorkspaceMemberRole.Admin)]
    public async Task APrivateDocument_StaysVisibleToOwnersAndAdmins(WorkspaceMemberRole role)
    {
        var document = Document(WorkspaceDocumentStatus.@private);
        var member = Member(Guid.NewGuid());

        Assert.True((await Evaluate(document, member, WorkspaceDocumentPermissions.View, role)).IsSuccess);
        Assert.True((await Evaluate(document, member, WorkspaceDocumentPermissions.Download, role)).IsSuccess);
    }

    [Fact]
    public async Task APrivateDocument_StaysVisibleToItsUploader()
    {
        var document = Document(WorkspaceDocumentStatus.@private);
        var uploader = Member(_uploaderId);

        Assert.True((await Evaluate(document, uploader, WorkspaceDocumentPermissions.View)).IsSuccess);
        Assert.True((await Evaluate(document, uploader, WorkspaceDocumentPermissions.Download)).IsSuccess);
    }

    [Fact]
    public void APrivateDocument_IsNotIndexEligible()
    {
        Assert.False(Document(WorkspaceDocumentStatus.@private).IsIndexEligible());
        Assert.True(Document(WorkspaceDocumentStatus.@public).IsIndexEligible());
    }
}

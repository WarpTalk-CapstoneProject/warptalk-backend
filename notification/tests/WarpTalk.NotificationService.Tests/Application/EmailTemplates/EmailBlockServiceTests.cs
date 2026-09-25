using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

public sealed class EmailBlockServiceTests
{
    private static readonly WarpTalk.Shared.Authorization.AdminActorContext Admin = InMemoryEmailCmsStore.Admin;
    private const string LayoutHtml = "<html><head></head><body>{{content}}</body></html>";

    private readonly InMemoryEmailCmsStore _store = new();

    private EmailBlockService Blocks() => new(_store.UnitOfWork.Object, NullLogger<EmailBlockService>.Instance);

    private EmailContentService Content() =>
        new(_store.UnitOfWork.Object, NullLogger<EmailContentService>.Instance);

    private async Task<EmailBlockDto> Layout(string key, bool publish = true)
    {
        var created = (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("LAYOUT", key, key, null, LayoutHtml, null, null))).Value!;
        return publish ? (await Blocks().PublishAsync(Admin, created.Id, new PublishEmailRequest())).Value! : created;
    }

    [Theory]
    [InlineData("<html><body>no slot</body></html>", "exactly once")]
    [InlineData("<html><body>{{content}}{{content}}</body></html>", "exactly once")]
    [InlineData("<html><body>{{content}} {{FullName}}</body></html>", "not a layout slot")]
    [InlineData("<html><body>{{content}}<script>x()</script></body></html>", "<script> is not allowed")]
    public async Task ALayoutThatCannotWrapAnEmail_CannotBePublished(string html, string expected)
    {
        var created = (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("LAYOUT", "bad", "Bad", null, html, null, null))).Value!;

        var publish = await Blocks().PublishAsync(Admin, created.Id, new PublishEmailRequest());

        Assert.Equal(ErrorCodes.ValidationError, publish.ErrorCode);
        Assert.Contains(expected, publish.Error);
    }

    [Fact]
    public async Task ALayoutMayCarryStyleForItsHead_ButABlockMayNot()
    {
        var layout = (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("LAYOUT", "styled", "Styled", null,
            "<html><head><style>p { color: red; }</style></head><body>{{content}}</body></html>", null, null))).Value!;
        var block = (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("PARTIAL", "styled-block", "Styled block", null,
            "<style>p { color: red; }</style><p>x</p>", null, null))).Value!;

        Assert.True((await Blocks().PublishAsync(Admin, layout.Id, new PublishEmailRequest())).IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, (await Blocks().PublishAsync(Admin, block.Id, new PublishEmailRequest())).ErrorCode);
    }

    [Fact]
    public async Task Keys_AreUniquePerKind_AndMustBeSlugs()
    {
        await Layout("brand");

        Assert.Equal(ErrorCodes.Conflict, (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("LAYOUT", "brand", "x", null, LayoutHtml, null, null))).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("PARTIAL", "Has Spaces", "x", null, "<p/>", null, null))).ErrorCode);
        Assert.True((await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("PARTIAL", "brand", "x", null, "<p>ok</p>", null, null))).IsSuccess);
    }

    [Fact]
    public async Task TheDefaultLayout_MustBePublished_AndOnlyOneIsDefault()
    {
        var first = await Layout("first");
        var second = await Layout("second");
        var draftOnly = await Layout("draft-only", publish: false);

        Assert.Equal(ErrorCodes.InvalidState, (await Blocks().SetDefaultAsync(Admin, draftOnly.Id)).ErrorCode);
        Assert.True((await Blocks().SetDefaultAsync(Admin, second.Id)).IsSuccess);

        Assert.False(_store.Blocks.Single(b => b.Id == first.Id).IsDefault);
        Assert.True(_store.Blocks.Single(b => b.Id == second.Id).IsDefault);
    }

    [Fact]
    public async Task ABlockInUse_CannotBeArchivedOrDeleted()
    {
        var layout = await Layout("brand");
        await Content().SaveDraftAsync(Admin, EmailTemplateCatalog.AuthPasswordReset, "en",
            new SaveEmailDraftRequest("S", "", "", "<a href=\"{{ResetUrl}}\">x</a>", null, layout.Id));
        var spare = await Layout("spare");

        var archive = await Blocks().ArchiveAsync(Admin, layout.Id);
        var deletePublished = await Blocks().DeleteAsync(Admin, spare.Id);

        Assert.Equal(ErrorCodes.InvalidState, archive.ErrorCode);
        Assert.Contains("auth.password-reset (en)", archive.Error);
        Assert.Equal(ErrorCodes.InvalidState, deletePublished.ErrorCode);
    }

    [Fact]
    public async Task AnUnusedDraftBlock_CanBeDeleted_AndDiscardGoesBackToPublished()
    {
        var draft = await Layout("scratch", publish: false);
        Assert.True((await Blocks().DeleteAsync(Admin, draft.Id)).IsSuccess);

        var live = await Layout("live");
        await Blocks().SaveDraftAsync(Admin, live.Id, new SaveEmailBlockDraftRequest("live", null, "<html><body><b>{{content}}</b></body></html>", null, null));
        var discarded = await Blocks().DiscardDraftAsync(Admin, live.Id);

        Assert.Equal(LayoutHtml, discarded.Value!.Draft.Html);
        Assert.False(discarded.Value.HasDraftChanges);
    }

    [Fact]
    public async Task Versions_RecordEachPublish_AndRestoreIntoTheDraft()
    {
        var layout = await Layout("brand");
        await Blocks().SaveDraftAsync(Admin, layout.Id, new SaveEmailBlockDraftRequest("brand", null, "<html><body><i>{{content}}</i></body></html>", null, null));
        await Blocks().PublishAsync(Admin, layout.Id, new PublishEmailRequest("italics"));

        var versions = (await Blocks().ListVersionsAsync(layout.Id)).Value!;
        var restored = await Blocks().RestoreVersionAsync(Admin, layout.Id, 1);

        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.Version));
        Assert.Equal(LayoutHtml, restored.Value!.Draft.Html);
        Assert.True(restored.Value.HasDraftChanges);
    }

    [Fact]
    public async Task UsedBy_NamesTheEmailsAndLayoutsAPublishWouldChange()
    {
        var signature = (await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("PARTIAL", "signature", "Signature", null, "<p>Team</p>", null, null))).Value!;
        await Blocks().CreateAsync(Admin, new CreateEmailBlockRequest("LAYOUT", "footer-layout", "Footer layout", null,
            "<html><body>{{content}}{{> signature}}</body></html>", null, null));
        await Content().SaveDraftAsync(Admin, EmailTemplateCatalog.MeetingInvitation, "ja",
            new SaveEmailDraftRequest("S", "", "", "<a href=\"{{MeetingLink}}\">x</a>{{> signature}}", null, null));

        var usedBy = (await Blocks().GetAsync(signature.Id)).Value!.UsedBy;

        Assert.Contains("meeting.invitation (ja)", usedBy);
        Assert.Contains("layout: Footer layout", usedBy);
    }

    [Fact]
    public async Task Preview_RendersADraftLayoutAroundAnEmail_InDarkMode()
    {
        var preview = (await Blocks().PreviewAsync(null, "LAYOUT", new EmailBlockPreviewRequest(
            "<html><head></head><body class=\"x\">{{content}}</body></html>", null, ".x { background: #000; }",
            EmailTemplateCatalog.AuthVerifyEmail, "en", Dark: true))).Value!;

        Assert.Contains("<body class=\"x\">", preview.Html);
        Assert.Contains("<style>.x { background: #000; }</style>", preview.Html);
        Assert.Contains("Verify Email Address", preview.Html);
        Assert.Empty(preview.Issues);
    }

    [Fact]
    public async Task BulkDuplicate_FindsAFreeKey()
    {
        var layout = await Layout("brand");

        var result = (await Blocks().BulkAsync(Admin, new EmailBlockBulkRequest("duplicate", [layout.Id, layout.Id]))).Value!;

        Assert.Equal(1, result.Succeeded);
        Assert.Contains(_store.Blocks, b => b.Key == "brand-copy");
    }
}

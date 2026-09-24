using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

public sealed class EmailContentServiceTests
{
    private const string Key = EmailTemplateCatalog.AuthPasswordReset;
    private static readonly WarpTalk.Shared.Authorization.AdminActorContext Admin = InMemoryEmailCmsStore.Admin;

    private readonly InMemoryEmailCmsStore _store = new();
    private readonly Mock<IEmailSender> _sender = new();

    private EmailContentService Service() =>
        new(_store.UnitOfWork.Object, NullLogger<EmailContentService>.Instance, _sender.Object);

    private static SaveEmailDraftRequest Draft(string subject = "Reset it", string body = "<a href=\"{{ResetUrl}}\">Reset</a>", DateTime? expected = null) =>
        new(subject, "Pick a new one", "New password", body, null, null, expected);

    [Fact]
    public async Task List_ShowsEveryCatalogEmail_WithItsLocalesAndPendingDrafts()
    {
        await Service().SaveDraftAsync(Admin, Key, "vi", Draft());

        var list = (await Service().ListAsync()).Value!;

        Assert.Equal(EmailTemplateCatalog.All.Select(d => d.Key), list.Select(item => item.Key));
        var reset = list.Single(item => item.Key == Key);
        Assert.True(reset.HasDraftChanges);
        Assert.Equal("vi", Assert.Single(reset.Variants).Locale);
        Assert.Equal("Reset your WarpTalk password", reset.Subject); // nothing published in English yet
        Assert.Equal("Resend", reset.Provider);
    }

    [Fact]
    public async Task SavingOverSomeoneElsesNewerDraft_IsAConflict()
    {
        var service = Service();
        var first = (await service.SaveDraftAsync(Admin, Key, "en", Draft("First"))).Value!;
        await service.SaveDraftAsync(Admin, Key, "en", Draft("Second", expected: first.DraftUpdatedAt));

        var stale = await service.SaveDraftAsync(Admin, Key, "en", Draft("Mine", expected: first.DraftUpdatedAt.AddSeconds(-5)));

        Assert.Equal(ErrorCodes.Conflict, stale.ErrorCode);
        Assert.Equal("Second", _store.Variants.Single().DraftSubject);
    }

    [Theory]
    [InlineData("<a href=\"{{ResetUrl}}\">Reset</a> {{Nope}}", "is not a variable")]
    [InlineData("<p>No link at all</p>", "{{ResetUrl}} must appear")]
    [InlineData("<a href=\"{{ResetUrl}}\">x</a><script>alert(1)</script>", "<script> is not allowed")]
    [InlineData("<a href=\"{{ResetUrl}}\">x</a>{{> missing-block}}", "is not a published block")]
    public async Task Publish_RefusesAnEmailThatWouldGoOutBrokenOrUnsafe(string body, string expected)
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft(body: body));

        var result = await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest());

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains(expected, result.Error);
        Assert.Equal(0, _store.Variants.Single().PublishedVersion);
    }

    [Fact]
    public async Task Publish_KeepsAVersionPerPublish_AndRestoreBringsOneBackIntoTheDraft()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft("One"));
        await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest("v1 note"));
        await service.SaveDraftAsync(Admin, Key, "en", Draft("Two"));
        await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest(ExpectedPublishedVersion: 1));

        var versions = (await service.ListVersionsAsync(Key, "en")).Value!;
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.Version));
        Assert.Equal("One", versions[1].Fields["subject"]);
        Assert.Equal("v1 note", versions[1].Note);

        var restored = await service.RestoreVersionAsync(Admin, Key, "en", 1);

        Assert.True(restored.IsSuccess, restored.Error);
        Assert.Equal("One", restored.Value!.Draft.Subject);
        Assert.Equal("Two", restored.Value.Published!.Subject); // restore edits the draft; publishing is separate
        Assert.True(restored.Value.HasDraftChanges);
    }

    [Fact]
    public async Task Publish_OverSomeoneElsesNewerPublish_IsAConflict()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft());
        await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest());

        var stale = await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest(ExpectedPublishedVersion: 0));

        Assert.Equal(ErrorCodes.Conflict, stale.ErrorCode);
    }

    [Fact]
    public async Task DiscardDraft_GoesBackToThePublishedVersion_OrRemovesANeverPublishedLocale()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft("Live"));
        await service.PublishAsync(Admin, Key, "en", new PublishEmailRequest());
        await service.SaveDraftAsync(Admin, Key, "en", Draft("Scratch"));
        await service.SaveDraftAsync(Admin, Key, "ja", Draft("JA scratch"));

        var en = await service.DiscardDraftAsync(Admin, Key, "en");
        var ja = await service.DiscardDraftAsync(Admin, Key, "ja");

        Assert.Equal("Live", en.Value!.Draft.Subject);
        Assert.False(en.Value.HasDraftChanges);
        Assert.Null(ja.Value);
        Assert.DoesNotContain(_store.Variants, v => v.Locale == "ja");
    }

    [Fact]
    public async Task Duplicate_CopiesADraftIntoAnotherLocale_WithoutOverwritingUnlessAsked()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft("English"));

        var copy = await service.DuplicateAsync(Admin, Key, "en", new DuplicateEmailVariantRequest("vi"));
        var again = await service.DuplicateAsync(Admin, Key, "en", new DuplicateEmailVariantRequest("vi"));
        var overwrite = await service.DuplicateAsync(Admin, Key, "en", new DuplicateEmailVariantRequest("vi", Overwrite: true));

        Assert.True(copy.IsSuccess, copy.Error);
        Assert.Equal("English", copy.Value!.Draft.Subject);
        Assert.Equal(0, copy.Value.PublishedVersion);
        Assert.Equal(ErrorCodes.Conflict, again.ErrorCode);
        Assert.True(overwrite.IsSuccess);
    }

    [Fact]
    public async Task ResetToDefault_PutsTheBuiltInWordingInTheDraft()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft("Custom"));

        var reset = await service.ResetToDefaultAsync(Admin, Key, "en");

        Assert.Equal("Reset your WarpTalk password", reset.Value!.Draft.Subject);
    }

    [Fact]
    public async Task AnArchivedLocale_CannotBeEditedOrPublished_UntilRestored()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "vi", Draft());
        await service.ArchiveAsync(Admin, Key, "vi");

        Assert.Equal(ErrorCodes.InvalidState, (await service.SaveDraftAsync(Admin, Key, "vi", Draft("x"))).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidState, (await service.PublishAsync(Admin, Key, "vi", new PublishEmailRequest())).ErrorCode);

        Assert.True((await service.UnarchiveAsync(Admin, Key, "vi")).IsSuccess);
        Assert.True((await service.PublishAsync(Admin, Key, "vi", new PublishEmailRequest())).IsSuccess);
    }

    [Fact]
    public async Task UnknownEmailsAndLocales_AreRefused()
    {
        Assert.Equal(ErrorCodes.NotFound, (await Service().SaveDraftAsync(Admin, "billing.invoice", "en", Draft())).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, (await Service().SaveDraftAsync(Admin, Key, "fr", Draft())).ErrorCode);
    }

    [Fact]
    public async Task Preview_UsesTheNamedSampleSet_AndCanForceDarkMode()
    {
        var service = Service();
        var set = await service.SaveSampleSetAsync(Admin, Key, null, new SaveSampleDataSetRequest("Long name",
            new Dictionary<string, string> { ["FullName"] = "Nguyễn Thị Minh Khai" }));
        Assert.True(set.IsSuccess, set.Error);

        var preview = (await service.PreviewAsync(Key, new EmailPreviewRequest(
            "en", "Hi {{FullName}}", "For {{FullName}}", "Heading", "<a href=\"{{ResetUrl}}\">x</a>", null, null,
            set.Value!.Id, Dark: true))).Value!;

        Assert.Equal("Hi Nguyễn Thị Minh Khai", preview.Subject);
        Assert.Equal("For Nguyễn Thị Minh Khai", preview.Preheader);
        Assert.Contains("<meta name=\"color-scheme\" content=\"dark\">", preview.Html);
        Assert.Empty(preview.Issues);
        Assert.Equal("Built-in layout", preview.LayoutName);
    }

    [Fact]
    public async Task SampleSets_OnlyTakeTheEmailsOwnVariables()
    {
        var result = await Service().SaveSampleSetAsync(Admin, Key, null,
            new SaveSampleDataSetRequest("Bad", new Dictionary<string, string> { ["MeetingTitle"] = "x" }));

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SendTest_GoesToEveryAddressGiven_FilledWithSamples()
    {
        var sent = new List<EmailMessage>();
        _sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent.Add(message))
            .ReturnsAsync(true);

        var result = await Service().SendTestAsync(Admin, Key, new EmailTestSendRequest(
            "en", "Reset for {{FullName}}", "", "H", "<a href=\"{{ResetUrl}}\">Reset</a>", null, null,
            ["qa@warptalk.vn", "owner@warptalk.vn", "QA@warptalk.vn"]));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "qa@warptalk.vn", "owner@warptalk.vn" }, sent.Select(m => m.ToEmail));
        Assert.All(sent, message => Assert.Equal("[Test] Reset for Linh Nguyen", message.Subject));
        Assert.Empty(_store.Variants); // a test saves nothing
    }

    [Theory]
    [InlineData(new string[0], "at least one address")]
    [InlineData(new[] { "not-an-address" }, "is not an email address")]
    [InlineData(new[] { "a@x.vn", "b@x.vn", "c@x.vn", "d@x.vn", "e@x.vn", "f@x.vn" }, "5 addresses or fewer")]
    public async Task SendTest_RefusesBadRecipients(string[] recipients, string expected)
    {
        var result = await Service().SendTestAsync(Admin, Key, new EmailTestSendRequest(
            "en", "S", "", "H", "<a href=\"{{ResetUrl}}\">x</a>", null, null, recipients));

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains(expected, result.Error);
        _sender.Verify(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bulk_PublishesEveryPendingDraft_AndReportsEachEmail()
    {
        var service = Service();
        await service.SaveDraftAsync(Admin, Key, "en", Draft());
        await service.SaveDraftAsync(Admin, Key, "vi", Draft("VI"));
        await service.SaveDraftAsync(Admin, EmailTemplateCatalog.AuthVerifyEmail, "en",
            new SaveEmailDraftRequest("S", "", "", "<p>no link</p>", null, null));

        var result = (await service.BulkAsync(Admin, new EmailBulkRequest(
            "publish", [Key, EmailTemplateCatalog.AuthVerifyEmail, EmailTemplateCatalog.MeetingReminder]))).Value!;

        Assert.True(result.Items[0].Succeeded, result.Items[0].Error);
        Assert.False(result.Items[1].Succeeded);
        Assert.Contains("VerifyUrl", result.Items[1].Error);
        Assert.False(result.Items[2].Succeeded); // nothing to publish
        Assert.All(_store.Variants.Where(v => v.TemplateKey == Key), v => Assert.Equal(1, v.PublishedVersion));
    }

    [Fact]
    public void SendsNeverUseDraftsOrArchivedRows_InTheRepositoryContract()
    {
        // The in-memory store mirrors EmailContentVariantRepository.GetPublishedAsync; this pins the rule.
        _store.Variants.Add(new() { TemplateKey = Key, Locale = "en", Status = EmailCmsConstants.StatusActive, DraftSubject = "d", DraftPreheader = "", DraftHeading = "", DraftBodyHtml = "b" });
        Assert.Null(_store.UnitOfWork.Object.EmailContentVariantRepository.GetPublishedAsync(Key, "en").Result);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>
/// Email CMS v3: admin-created templates, the stored-email render behind thumbnails and the
/// "as received" preview, deletion rules, and audience sends through the worker.
/// </summary>
public sealed class EmailCustomTemplateTests
{
    private const string Key = "promo.autumn-launch";
    private static readonly WarpTalk.Shared.Authorization.AdminActorContext Admin = InMemoryEmailCmsStore.Admin;

    private readonly InMemoryEmailCmsStore _store = new();
    private readonly Mock<IEmailSender> _sender = new();
    private readonly List<EmailMessage> _sent = [];
    private readonly FakeAudience _audience = new();

    public EmailCustomTemplateTests()
    {
        _sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EmailMessage message, CancellationToken _) =>
            {
                _sent.Add(message);
                return !message.ToEmail.StartsWith("bounce", StringComparison.Ordinal);
            });
    }

    private EmailContentService Content() =>
        new(_store.UnitOfWork.Object, NullLogger<EmailContentService>.Instance, _sender.Object,
            envelope: new EmailEnvelope("WarpTalk", "hello@warptalk.vn"));

    private EmailCustomTemplateService Custom() => new(_store.UnitOfWork.Object, Content());

    private EmailCampaignService Sends(EmailCampaignOptions? options = null) =>
        new(_store.UnitOfWork.Object, new EmailDefinitionProvider(_store.UnitOfWork.Object), _audience,
            new EmailPublishedResolver(_store.UnitOfWork.Object), options, _sender.Object);

    private static CreateCustomEmailTemplateRequest Request(
        string key = Key, string category = EmailCmsConstants.CategoryMarketing, IReadOnlyList<CustomEmailVariableRequest>? variables = null) =>
        new(key, "Autumn launch", "Tell everyone about the launch.", category,
            variables ?? [new("EventDate", "Launch date", "DATE", "2026-10-01", Required: true), new("SignupUrl", "Sign-up link", "URL", "https://warptalk.vn/launch")],
            null,
            [
                new("en", "Hi {{RecipientName}}, we launch on {{EventDate}}", "Save the date", "Autumn launch", "<p>Join us: <a href=\"{{SignupUrl}}\">sign up</a></p>", null),
                new("vi", "Chào {{RecipientName}}", null, null, "<p>Tham gia: {{SignupUrl}}</p>", null),
            ]);

    private async Task CreateAndPublishAsync(string category = EmailCmsConstants.CategoryMarketing)
    {
        Assert.True((await Custom().CreateAsync(Admin, Request(category: category))).IsSuccess);
        Assert.True((await Content().PublishAsync(Admin, Key, "en", new PublishEmailRequest())).IsSuccess);
    }

    // ── Create ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_SavesDraftsAndListsItAsCustom_WithASubjectAnAdminCanRead()
    {
        var created = await Custom().CreateAsync(Admin, Request());

        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal(2, _store.Variants.Count(v => v.TemplateKey == Key));
        Assert.All(_store.Variants, v => Assert.Equal(0, v.PublishedVersion)); // drafts only: nothing goes out yet
        var item = (await Content().ListAsync()).Value!.Single(i => i.Key == Key);
        Assert.True(item.IsCustom);
        Assert.Equal(EmailCmsConstants.CategoryMarketing, item.Category);
        Assert.Equal("Hi Linh Nguyen, we launch on 2026-10-01", item.RenderedSubject);
        Assert.DoesNotContain("{{", item.RenderedSubject);
        Assert.Contains(item.Variables, v => v.Name == "RecipientName" && v.Implicit);
        Assert.Contains(item.Variables, v => v.Name == "SignupUrl" && v.Type == "URL" && !v.Implicit);
    }

    [Theory]
    [InlineData(EmailTemplateCatalog.AuthPasswordReset, ErrorCodes.Conflict)]
    [InlineData("A", ErrorCodes.ValidationError)]
    [InlineData("has space", ErrorCodes.ValidationError)]
    public async Task Create_RefusesABuiltInOrMalformedKey(string key, string code)
    {
        var result = await Custom().CreateAsync(Admin, Request(key: key));
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task Create_RefusesADuplicateKeyAnImplicitVariableAndAMissingEnglishVersion()
    {
        await Custom().CreateAsync(Admin, Request());
        Assert.Equal(ErrorCodes.Conflict, (await Custom().CreateAsync(Admin, Request())).ErrorCode);

        var implicitName = await Custom().CreateAsync(Admin, Request(key: "other", variables: [new("RecipientName", null, null, "x")]));
        Assert.Contains("filled in automatically", implicitName.Error);

        var noEnglish = await Custom().CreateAsync(Admin, Request(key: "third") with { Content = [new("vi", "Chào", null, null, "<p>x</p>", null)] });
        Assert.Contains("English", noEnglish.Error);
    }

    // ── Render (thumbnails and the inbox preview) ─────────────────────────────────────────────

    [Fact]
    public async Task Render_ShowsWhatIsSent_WithTheInboxEnvelope()
    {
        var builtIn = (await Content().RenderAsync(EmailTemplateCatalog.AuthPasswordReset, new EmailRenderQuery("vi"))).Value!;
        Assert.Equal("BUILT_IN", builtIn.SourceUsed);
        Assert.Equal("en", builtIn.LocaleUsed);
        Assert.Equal("hello@warptalk.vn", builtIn.FromAddress);
        Assert.DoesNotContain("{{", builtIn.Subject);

        await Custom().CreateAsync(Admin, Request());
        var draft = (await Content().RenderAsync(Key, new EmailRenderQuery("vi"))).Value!;
        Assert.Equal("DRAFT", draft.SourceUsed); // never published: the draft, labelled as such
        Assert.Equal("vi", draft.LocaleUsed);

        await Content().PublishAsync(Admin, Key, "en", new PublishEmailRequest());
        var sent = (await Content().RenderAsync(Key, new EmailRenderQuery("vi"))).Value!;
        Assert.Equal("PUBLISHED", sent.SourceUsed); // vi has no published version: English goes out
        Assert.Equal("en", sent.LocaleUsed);
        Assert.Equal("Hi Linh Nguyen, we launch on 2026-10-01", sent.Subject);
        Assert.Contains("https://warptalk.vn/launch", sent.Html);
        Assert.Equal("linh@example.com", sent.ToAddress);
    }

    // ── Delete and restore ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuiltInTemplates_CannotBeDeletedOrResetIntoNothing()
    {
        var deleted = await Custom().DeleteAsync(Admin, EmailTemplateCatalog.AuthPasswordReset, new DeleteCustomEmailTemplateRequest("tidy"));
        Assert.Equal(ErrorCodes.InvalidState, deleted.ErrorCode);
        Assert.Contains("archive it", deleted.Error);

        await Custom().CreateAsync(Admin, Request());
        var reset = await Content().ResetToDefaultAsync(Admin, Key, "en");
        Assert.Equal(ErrorCodes.InvalidState, reset.ErrorCode);
    }

    [Fact]
    public async Task Delete_IsSoftWithAReason_AndCanBeUndone()
    {
        await Custom().CreateAsync(Admin, Request());
        Assert.Equal(ErrorCodes.ValidationError, (await Custom().DeleteAsync(Admin, Key, new DeleteCustomEmailTemplateRequest(" "))).ErrorCode);

        Assert.True((await Custom().DeleteAsync(Admin, Key, new DeleteCustomEmailTemplateRequest("Campaign over"))).IsSuccess);
        var row = _store.Custom.Single();
        Assert.Equal(EmailCmsConstants.StatusDeleted, row.Status);
        Assert.Equal("Campaign over", row.DeleteReason);
        var listed = (await Content().ListAsync()).Value!.Single(i => i.Key == Key);
        Assert.Equal("DELETED", listed.Status); // shown under Archived, not gone
        Assert.Equal(ErrorCodes.InvalidState, (await Content().SaveDraftAsync(Admin, Key, "en",
            new SaveEmailDraftRequest("x", "", "", "<p>x</p>", null, null, null))).ErrorCode);

        Assert.True((await Custom().RestoreAsync(Admin, Key)).IsSuccess);
        Assert.Equal(EmailCmsConstants.StatusActive, _store.Custom.Single().Status);
    }

    [Fact]
    public async Task DeleteForGood_OnlyWhenItNeverSentAnything()
    {
        await Custom().CreateAsync(Admin, Request());
        Assert.True((await Custom().CheckDeletionAsync(Key)).Value!.CanDeletePermanently);

        _store.Stats.Add(new EmailDeliveryStat { TemplateKey = Key, Locale = "en", Day = DateOnly.FromDateTime(DateTime.UtcNow), SentCount = 3 });
        var check = (await Custom().CheckDeletionAsync(Key)).Value!;
        Assert.False(check.CanDeletePermanently);
        Assert.Equal(ErrorCodes.InvalidState, (await Custom().DeleteAsync(Admin, Key, new DeleteCustomEmailTemplateRequest("gone", Permanent: true))).ErrorCode);

        _store.Stats.Clear();
        Assert.True((await Custom().DeleteAsync(Admin, Key, new DeleteCustomEmailTemplateRequest("mistake", Permanent: true))).IsSuccess);
        Assert.Empty(_store.Custom);
        Assert.DoesNotContain(_store.Variants, v => v.TemplateKey == Key);
    }

    // ── Audience sends ───────────────────────────────────────────────────────────────────────

    private static EmailAudienceDto Everyone => new("ALL", null, null, null, null, null);

    [Fact]
    public async Task Sends_OnlyCustomPublishedTemplates()
    {
        var builtIn = await Sends().EstimateAsync(EmailTemplateCatalog.AuthPasswordReset, new(Everyone));
        Assert.Contains("not to an audience", builtIn.Error);

        await Custom().CreateAsync(Admin, Request());
        var unpublished = await Sends().EstimateAsync(Key, new(Everyone));
        Assert.Contains("Publish the English version", unpublished.Error);
    }

    [Fact]
    public async Task Send_ReachesEachPersonInTheirLanguage_SkipsOptOuts_AndLogsEveryOutcome()
    {
        await CreateAndPublishAsync();
        await Content().SaveDraftAsync(Admin, Key, "vi", new SaveEmailDraftRequest("Chào {{RecipientName}}", "", "", "<p>VI {{SignupUrl}}</p>", null, null, null));
        await Content().PublishAsync(Admin, Key, "vi", new PublishEmailRequest());
        var optedOut = Guid.NewGuid();
        _audience.Members.AddRange([
            new(Guid.NewGuid(), "an@example.com", "An Tran", "vi"),
            new(Guid.NewGuid(), "ken@example.com", "Ken Sato", "ja"),
            new(Guid.NewGuid(), "bounce@example.com", null, "en"),
            new(optedOut, "quiet@example.com", "Quiet", "en"),
        ]);
        _store.OptOuts.Add((optedOut, NotificationConstants.TypePromotion));

        var estimate = (await Sends().EstimateAsync(Key, new(Everyone))).Value!;
        Assert.Equal(3, estimate.Recipients);
        Assert.Equal(1, estimate.SkippedOptedOut);
        Assert.Contains("ja", estimate.FallbackLocales);

        var campaign = await Sends().CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, new Dictionary<string, string> { ["EventDate"] = "Oct 1" }, 3));
        Assert.True(campaign.IsSuccess, campaign.Error);
        Assert.Empty(_sent); // nothing leaves until the worker runs

        var options = new EmailCampaignOptions { SendsPerMinute = 60 };
        while (await Sends(options).ProcessNextAsync(batchSize: 2)) { }

        var row = _store.Campaigns.Single();
        Assert.Equal(EmailCmsConstants.CampaignCompleted, row.Status);
        Assert.Equal((4, 2, 1, 1), (row.TotalCount, row.SentCount, row.FailedCount, row.SkippedCount));
        Assert.Equal(3, _sent.Count);
        Assert.Contains(_sent, m => m.ToEmail == "an@example.com" && m.Subject == "Chào An Tran");
        Assert.Contains(_sent, m => m.ToEmail == "ken@example.com" && m.Subject == "Hi Ken Sato, we launch on Oct 1"); // ja → English
        Assert.Contains(_sent, m => m.ToEmail == "bounce@example.com" && m.Subject.StartsWith("Hi bounce@example.com", StringComparison.Ordinal));
        Assert.Equal("Turned off promotional email.", _store.Recipients.Single(r => r.UserId == optedOut).Error);
        Assert.Equal(2, _store.Stats.Sum(s => s.SentCount)); // the 30-day counters include custom sends
    }

    [Fact]
    public async Task Send_IsRateLimited_AndRefusesADriftedCount_AndCanBeCancelled()
    {
        await CreateAndPublishAsync();
        _audience.Members.Add(new(Guid.NewGuid(), "a@example.com", "A", "en"));

        var drifted = await Sends().CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, null, 50));
        Assert.Equal(ErrorCodes.Conflict, drifted.ErrorCode);

        var options = new EmailCampaignOptions { MaxSendsPerAdminPerHour = 1 };
        var first = (await Sends(options).CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, null, 1))).Value!;
        Assert.Equal(ErrorCodes.RateLimitExceeded, (await Sends(options).CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, null, 1))).ErrorCode);

        var cancelled = (await Sends().CancelAsync(Admin, first.Id)).Value!;
        Assert.Equal(EmailCmsConstants.CampaignCancelled, cancelled.Status);
        Assert.Equal(1, cancelled.Skipped);
        Assert.False(await Sends().ProcessNextAsync(10));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Send_RefusesAMissingRequiredValueAndAMalformedLink()
    {
        await CreateAndPublishAsync();
        _audience.Members.Add(new(Guid.NewGuid(), "a@example.com", "A", "en"));

        var missing = await Sends().CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, new Dictionary<string, string> { ["EventDate"] = " " }, 1));
        Assert.Contains("Launch date", missing.Error);
        var badLink = await Sends().CreateAsync(Admin, Key, new CreateEmailSendRequest(Everyone, new Dictionary<string, string> { ["SignupUrl"] = "javascript:alert(1)" }, 1));
        Assert.Contains("https://", badLink.Error);
    }

    // ── Announcement email channel ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAnnouncementsEmailChannel_IsQueuedForItsStart_AndWithdrawnWhenUnpublished()
    {
        await CreateAndPublishAsync(EmailCmsConstants.CategoryAnnouncement);
        var announcements = new List<Announcement>();
        var repository = new Mock<IAnnouncementRepository>();
        repository.Setup(r => r.AddAsync(It.IsAny<Announcement>(), It.IsAny<CancellationToken>()))
            .Callback((Announcement a, CancellationToken _) => announcements.Add(a)).Returns(Task.CompletedTask);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => announcements.FirstOrDefault(a => a.Id == id));
        _store.UnitOfWork.Setup(u => u.AnnouncementRepository).Returns(repository.Object);
        var service = new AnnouncementService(_store.UnitOfWork.Object, Mock.Of<IViewerAudienceResolver>(),
            NullLogger<AnnouncementService>.Instance, campaigns: Sends());

        var refused = await service.CreateAsync(Admin, Upsert(EmailTemplateCatalog.AuthPasswordReset));
        Assert.Contains("Email channel", refused.Error);

        var created = (await service.CreateAsync(Admin, Upsert(Key))).Value!;
        var start = DateTime.UtcNow.AddDays(2);
        var published = (await service.PublishAsync(Admin, created.Id, new PublishAnnouncementRequest(start))).Value!;

        var campaign = _store.Campaigns.Single();
        Assert.Equal(published.EmailCampaignId, campaign.Id);
        Assert.Equal(EmailCmsConstants.CampaignSourceAnnouncement, campaign.Source);
        Assert.Equal(start, campaign.ScheduledAt, TimeSpan.FromSeconds(1));
        Assert.Contains("Live captions", campaign.Values); // the announcement fills its own variables

        await service.UnpublishAsync(Admin, created.Id);
        Assert.Equal(EmailCmsConstants.CampaignCancelled, campaign.Status);
        Assert.Null(announcements.Single().EmailCampaignId);
    }

    private static UpsertAnnouncementRequest Upsert(string emailKey) =>
        new("Live captions", "Now in **40** languages.", "FEATURE", "ALL", null, null, null, null, null, null, EmailTemplateKey: emailKey);

    /// <summary>A fixed audience, so send tests do not depend on the directory.</summary>
    private sealed class FakeAudience : IEmailAudienceResolver
    {
        public List<EmailAudienceMember> Members { get; } = [];

        public Task<Result<EmailAudienceResult>> ResolveAsync(EmailAudienceSpec spec, int maxRecipients, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new EmailAudienceResult(Members.ToList(), Members.Count)));
    }
}

public sealed class EmailAudienceResolverTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly Guid Newcomer = Guid.NewGuid();

    private sealed class Directory : IEmailRecipientDirectory
    {
        public Task<Result<IReadOnlyList<Guid>>> ListActiveUserIdsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>([Owner, Member, Newcomer, Owner]));
        public Task<Result<IReadOnlyList<Guid>>> ListMembersOfPlansAsync(IReadOnlyCollection<string> planSlugs, int limit, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>([Member]));
        public Task<Result<IReadOnlyList<Guid>>> ListMembersOfWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, int limit, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>([Owner]));
        public Task<EmailRecipientCandidate?> GetAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<EmailRecipientCandidate?>(userId == Owner ? new(Owner, "owner@x.vn", "Owner", "vi-VN", DateTime.UtcNow.AddYears(-1))
                : userId == Member ? new(Member, "member@x.vn", "Member", "ja", DateTime.UtcNow.AddYears(-1))
                : new(Newcomer, "new@x.vn", null, null, DateTime.UtcNow.AddDays(-2)));
    }

    private static EmailAudienceResolver Resolver()
    {
        var viewer = new Mock<IViewerAudienceResolver>();
        viewer.Setup(v => v.ResolveAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, bool _, CancellationToken _) =>
                Result.Success(new ViewerAudience([], [], id == Owner ? ["Owner"] : ["Member"])));
        return new EmailAudienceResolver(new Directory(), viewer.Object);
    }

    private static EmailAudienceSpec Spec(string mode = "ALL", string[]? roles = null, string[]? locales = null, int? newDays = null, string[]? plans = null) =>
        EmailAudienceSpec.From(new EmailAudienceDto(mode, plans, mode == "WORKSPACES" ? [Guid.NewGuid()] : null, roles, locales, newDays)).Spec!;

    [Fact]
    public async Task EachPersonOnce_InTheirOwnLanguage()
    {
        var all = (await Resolver().ResolveAsync(Spec(), 100)).Value!;
        Assert.Equal(3, all.Members.Count);
        Assert.Equal("vi", all.Members.Single(m => m.UserId == Owner).Locale);
        Assert.Equal("en", all.Members.Single(m => m.UserId == Newcomer).Locale);
    }

    [Fact]
    public async Task TheAnnouncementNarrowingRulesApply()
    {
        Assert.Equal([Owner], (await Resolver().ResolveAsync(Spec(roles: ["Owner"]), 100)).Value!.Members.Select(m => m.UserId));
        Assert.Equal([Member], (await Resolver().ResolveAsync(Spec(locales: ["ja"]), 100)).Value!.Members.Select(m => m.UserId));
        Assert.Equal([Newcomer], (await Resolver().ResolveAsync(Spec(newDays: 7), 100)).Value!.Members.Select(m => m.UserId));
        Assert.Equal([Member], (await Resolver().ResolveAsync(Spec("PLANS", plans: ["pro"]), 100)).Value!.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task AnAudienceOverTheCap_IsRefusedRatherThanCut()
    {
        var result = await Resolver().ResolveAsync(Spec(), 2);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }
}

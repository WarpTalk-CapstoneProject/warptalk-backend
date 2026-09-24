using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.Helpers.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Domain.Rules;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.Tests.Application.Announcements;

public sealed class AnnouncementServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdminActorContext Admin = new(Guid.NewGuid(), "test");
    private static readonly Guid AdminId = Admin.ActorId;
    private static readonly Guid ViewerId = Guid.NewGuid();

    private readonly List<Announcement> _announcements = [];
    private readonly List<AnnouncementViewerState> _states = [];
    private readonly List<(Guid Id, string Event)> _daily = [];
    private readonly List<AnnouncementAsset> _assets = [];
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IViewerAudienceResolver> _audience = new();

    public AnnouncementServiceTests()
    {
        var repository = new Mock<IAnnouncementRepository>();
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _announcements.FirstOrDefault(a => a.Id == id));
        repository.Setup(r => r.AddAsync(It.IsAny<Announcement>(), It.IsAny<CancellationToken>()))
            .Callback<Announcement, CancellationToken>((a, _) => _announcements.Add(a))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.Remove(It.IsAny<Announcement>()))
            .Callback<Announcement>(a => _announcements.Remove(a));
        repository.Setup(r => r.GetLiveAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime now, CancellationToken _) =>
                (IReadOnlyList<Announcement>)_announcements
                    .Where(AnnouncementLifecycle.HasEffectiveStatus(AnnouncementConstants.EffectivePublished, now).Compile())
                    .ToList());

        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                (IReadOnlyList<Announcement>)_announcements.Where(a => ids.Contains(a.Id)).ToList());

        var states = new Mock<IAnnouncementViewerStateRepository>();
        states.Setup(r => r.GetForUserAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                (IReadOnlyDictionary<Guid, AnnouncementViewerState>)_states.Where(s => s.UserId == userId && ids.Contains(s.AnnouncementId)).ToDictionary(s => s.AnnouncementId));
        states.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, Guid userId, CancellationToken _) => _states.FirstOrDefault(s => s.AnnouncementId == id && s.UserId == userId));
        states.Setup(r => r.AddAsync(It.IsAny<AnnouncementViewerState>(), It.IsAny<CancellationToken>()))
            .Callback<AnnouncementViewerState, CancellationToken>((s, _) => _states.Add(s))
            .Returns(Task.CompletedTask);
        states.Setup(r => r.GetTotalsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
            {
                var rows = _states.Where(s => s.AnnouncementId == id).ToList();
                return new AnnouncementViewerTotals(rows.Count(s => s.ImpressionCount > 0), rows.Sum(s => s.ImpressionCount),
                    rows.Count(s => s.DismissedAt != null), rows.Count(s => s.CtaClickCount > 0), rows.Sum(s => s.CtaClickCount), rows.Sum(s => s.SecondaryClickCount));
            });

        var daily = new Mock<IAnnouncementDailyStatRepository>();
        daily.Setup(r => r.IncrementAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DateOnly, string, CancellationToken>((id, _, type, _) => _daily.Add((id, type)))
            .Returns(Task.CompletedTask);
        daily.Setup(r => r.ListSinceAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, DateOnly _, CancellationToken _) => (IReadOnlyList<AnnouncementDailyStat>)
                [new AnnouncementDailyStat { AnnouncementId = id, Day = DateOnly.FromDateTime(Now), Impressions = _daily.Count(d => d.Id == id && d.Event == AnnouncementConstants.EventImpression) }]);

        var assets = new Mock<IAnnouncementAssetRepository>();
        assets.Setup(r => r.AddAsync(It.IsAny<AnnouncementAsset>(), It.IsAny<CancellationToken>()))
            .Callback<AnnouncementAsset, CancellationToken>((a, _) => _assets.Add(a))
            .Returns(Task.CompletedTask);
        assets.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _assets.FirstOrDefault(a => a.Id == id));

        _unitOfWork.Setup(u => u.AnnouncementRepository).Returns(repository.Object);
        _unitOfWork.Setup(u => u.AnnouncementViewerStateRepository).Returns(states.Object);
        _unitOfWork.Setup(u => u.AnnouncementDailyStatRepository).Returns(daily.Object);
        _unitOfWork.Setup(u => u.AnnouncementAssetRepository).Returns(assets.Object);

        _unitOfWork.Setup(u => u.SaveChangesAsync()).ReturnsAsync(1);
    }

    private AnnouncementService Service() =>
        new(_unitOfWork.Object, _audience.Object, NullLogger<AnnouncementService>.Instance, new FixedTime(Now));

    private static UpsertAnnouncementRequest Request(
        string audience = AnnouncementConstants.AudienceAll,
        IReadOnlyList<string>? plans = null,
        IReadOnlyList<Guid>? workspaces = null,
        DateTime? endsAt = null) =>
        new("Live captions in 40 languages", "**New:** pick any language.", "feature", audience,
            plans, workspaces, "Try it", "/settings/languages", null, endsAt);

    private Announcement Seed(
        string status,
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        string audience = AnnouncementConstants.AudienceAll,
        string[]? plans = null,
        Guid[]? workspaces = null)
    {
        var announcement = new Announcement
        {
            Id = Guid.NewGuid(),
            Title = "T",
            BodyMarkdown = "B",
            Type = AnnouncementConstants.TypeFeature,
            Status = status,
            Placement = AnnouncementConstants.PlacementTopBanner,
            Variant = "SUBTLE",
            AccentColor = "BRAND",
            Frequency = AnnouncementConstants.FrequencyUntilDismissed,
            Dismissible = true,
            AudienceMode = audience,
            AudiencePlanSlugs = plans ?? [],
            AudienceWorkspaceIds = workspaces ?? [],
            StartsAt = startsAt,
            EndsAt = endsAt,
            PublishedAt = status == AnnouncementConstants.StatusPublished ? Now.AddDays(-1) : null,
            CreatedAt = Now.AddDays(-2),
            UpdatedAt = Now.AddDays(-2),
        };
        _announcements.Add(announcement);
        return announcement;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT", null, null, "DRAFT")]
    [InlineData("ARCHIVED", null, null, "ARCHIVED")]
    [InlineData("PUBLISHED", null, null, "PUBLISHED")]
    [InlineData("PUBLISHED", 1, null, "SCHEDULED")]
    [InlineData("PUBLISHED", -2, -1, "ENDED")]
    [InlineData("PUBLISHED", -2, 1, "PUBLISHED")]
    public void EffectiveStatus_AndItsDatabaseForm_Agree(string stored, int? startHours, int? endHours, string expected)
    {
        var announcement = new Announcement
        {
            Status = stored,
            StartsAt = startHours is { } s ? Now.AddHours(s) : null,
            EndsAt = endHours is { } e ? Now.AddHours(e) : null,
        };

        Assert.Equal(expected, AnnouncementLifecycle.EffectiveStatus(announcement, Now));
        foreach (var status in AnnouncementConstants.EffectiveStatuses)
        {
            var matches = AnnouncementLifecycle.HasEffectiveStatus(status, Now).Compile()(announcement);
            Assert.Equal(status == expected, matches);
        }
    }

    [Fact]
    public async Task Create_NormalizesAndStoresADraft()
    {
        var result = await Service().CreateAsync(Admin, Request());

        Assert.True(result.IsSuccess, result.Error);
        var stored = Assert.Single(_announcements);
        Assert.Equal(AnnouncementConstants.StatusDraft, stored.Status);
        Assert.Equal(AnnouncementConstants.TypeFeature, stored.Type);
        Assert.Equal(AdminId, stored.CreatedBy);
        Assert.Equal("DRAFT", result.Value!.EffectiveStatus);
    }

    [Fact]
    public async Task PublishNow_MakesItLive_AndClearsAFutureStart()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft, startsAt: Now.AddDays(3));

        var result = await Service().PublishAsync(Admin, draft.Id, new PublishAnnouncementRequest());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("PUBLISHED", result.Value!.EffectiveStatus);
        Assert.Null(draft.StartsAt);
        Assert.Equal(Now, draft.PublishedAt);
        Assert.Equal(AdminId, draft.PublishedBy);
    }

    [Fact]
    public async Task Schedule_NeedsAFutureStart()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft);

        var past = await Service().PublishAsync(Admin, draft.Id, new PublishAnnouncementRequest(Now.AddMinutes(-1)));
        var future = await Service().PublishAsync(Admin, draft.Id, new PublishAnnouncementRequest(Now.AddHours(2)));

        Assert.Equal(ErrorCodes.ValidationError, past.ErrorCode);
        Assert.True(future.IsSuccess, future.Error);
        Assert.Equal("SCHEDULED", future.Value!.EffectiveStatus);
    }

    [Fact]
    public async Task Publish_WithAnEndAlreadyPassed_IsRefused()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft, endsAt: Now.AddHours(-1));

        var result = await Service().PublishAsync(Admin, draft.Id, new PublishAnnouncementRequest());

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(AnnouncementConstants.StatusDraft, draft.Status);
    }

    [Fact]
    public async Task Unpublish_Archive_AndDelete_FollowTheLifecycle()
    {
        var live = Seed(AnnouncementConstants.StatusPublished);
        var service = Service();

        Assert.Equal(ErrorCodes.InvalidState, (await service.DeleteAsync(Admin, live.Id)).ErrorCode);

        Assert.True((await service.ArchiveAsync(Admin, live.Id)).IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, (await service.ArchiveAsync(Admin, live.Id)).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidState, (await service.UpdateAsync(Admin, live.Id, Request())).ErrorCode);

        Assert.True((await service.UnpublishAsync(Admin, live.Id)).IsSuccess);
        Assert.Equal(AnnouncementConstants.StatusDraft, live.Status);
        Assert.Equal(ErrorCodes.InvalidState, (await service.UnpublishAsync(Admin, live.Id)).ErrorCode);

        Assert.True((await service.DeleteAsync(Admin, live.Id)).IsSuccess);
        Assert.Empty(_announcements);
    }

    [Fact]
    public async Task Duplicate_IsADraftCopyWithoutTheWindow()
    {
        var original = Seed(AnnouncementConstants.StatusPublished, startsAt: Now.AddDays(-1), endsAt: Now.AddDays(1),
            audience: AnnouncementConstants.AudiencePlans, plans: ["pro"]);

        var copy = (await Service().DuplicateAsync(Admin, original.Id)).Value!;

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Copy of T", copy.Title);
        Assert.Equal("DRAFT", copy.EffectiveStatus);
        Assert.Null(copy.StartsAt);
        Assert.Null(copy.EndsAt);
        Assert.Equal(["pro"], copy.AudiencePlanSlugs);
    }

    // ── What the app shows ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Viewer_SeesOnlyLiveAnnouncementsMeantForThem()
    {
        var workspaceId = Guid.NewGuid();
        var everyone = Seed(AnnouncementConstants.StatusPublished);
        var theirPlan = Seed(AnnouncementConstants.StatusPublished, audience: AnnouncementConstants.AudiencePlans, plans: ["pro"]);
        var otherPlan = Seed(AnnouncementConstants.StatusPublished, audience: AnnouncementConstants.AudiencePlans, plans: ["enterprise"]);
        var theirWorkspace = Seed(AnnouncementConstants.StatusPublished, audience: AnnouncementConstants.AudienceWorkspaces, workspaces: [workspaceId]);
        var otherWorkspace = Seed(AnnouncementConstants.StatusPublished, audience: AnnouncementConstants.AudienceWorkspaces, workspaces: [Guid.NewGuid()]);
        Seed(AnnouncementConstants.StatusDraft);
        Seed(AnnouncementConstants.StatusArchived);
        Seed(AnnouncementConstants.StatusPublished, startsAt: Now.AddHours(1));
        Seed(AnnouncementConstants.StatusPublished, endsAt: Now.AddHours(-1));

        _audience.Setup(a => a.ResolveAsync(ViewerId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ViewerAudience([workspaceId], ["PRO"], ["Member"])));

        var visible = (await Service().GetActiveForViewerAsync(ViewerId)).Value!.Select(a => a.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { everyone.Id, theirPlan.Id, theirWorkspace.Id }, visible);
        Assert.DoesNotContain(otherPlan.Id, visible);
        Assert.DoesNotContain(otherWorkspace.Id, visible);
    }

    [Fact]
    public async Task Viewer_WhenTheirWorkspacesCannotBeResolved_SeesOnlyAnnouncementsForEveryone()
    {
        var everyone = Seed(AnnouncementConstants.StatusPublished);
        Seed(AnnouncementConstants.StatusPublished, audience: AnnouncementConstants.AudiencePlans, plans: ["pro"]);
        _audience.Setup(a => a.ResolveAsync(ViewerId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ViewerAudience>("down", ErrorCodes.ServiceUnavailable));

        var visible = (await Service().GetActiveForViewerAsync(ViewerId)).Value!;

        Assert.Equal(everyone.Id, Assert.Single(visible).Id);
    }

    [Fact]
    public async Task Viewer_WhoOnlyHasEveryoneAnnouncements_NeverCostsAWorkspaceLookup()
    {
        Seed(AnnouncementConstants.StatusPublished);

        await Service().GetActiveForViewerAsync(ViewerId);

        _audience.Verify(a => a.ResolveAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dismissed_IsNotShownAgain_AndDismissingTwiceIsHarmless()
    {
        var first = Seed(AnnouncementConstants.StatusPublished);
        var second = Seed(AnnouncementConstants.StatusPublished);
        var service = Service();

        Assert.True((await service.RecordEventAsync(ViewerId, first.Id, new AnnouncementEventRequest("DISMISS", "s1"))).IsSuccess);
        Assert.True((await service.RecordEventAsync(ViewerId, first.Id, new AnnouncementEventRequest("DISMISS", "s1"))).IsSuccess);

        Assert.NotNull(Assert.Single(_states).DismissedAt);
        Assert.Single(_daily); // the second dismissal in the same session is not counted again
        var visible = (await service.GetActiveForViewerAsync(ViewerId)).Value!;
        Assert.Equal(second.Id, Assert.Single(visible).Id);
        Assert.Equal(2, (await service.GetActiveForViewerAsync(Guid.NewGuid())).Value!.Count);
    }

    // ── Rules ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PLANS", null, "Choose at least one plan.")]
    [InlineData("PLANS", "Pro Plan", "is not a plan slug")]
    [InlineData("WORKSPACES", null, "Choose at least one workspace.")]
    [InlineData("EVERYONE", null, "Audience must be one of")]
    public void Rules_RefuseAnAudienceThatCannotBeResolved(string mode, string? plan, string expected)
    {
        var request = AnnouncementRules.Normalize(Request(mode, plans: plan is null ? null : [plan]));

        Assert.Contains(expected, AnnouncementRules.Validate(request));
    }

    [Theory]
    [InlineData("/settings", true)]
    [InlineData("https://warptalk.vn/blog/launch", true)]
    [InlineData("//evil.test/phish", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://files.test/a", false)]
    [InlineData("/has space", false)]
    public void Rules_AllowOnlyWebLinksAndInAppPaths(string link, bool allowed)
    {
        Assert.Equal(allowed, AnnouncementRules.IsAllowedLink(link));
    }

    [Fact]
    public void Rules_ClearTheAudienceListsTheModeDoesNotUse()
    {
        var request = AnnouncementRules.Normalize(Request(AnnouncementConstants.AudienceAll, plans: ["pro"], workspaces: [Guid.NewGuid()]));

        Assert.Empty(request.AudiencePlanSlugs!);
        Assert.Empty(request.AudienceWorkspaceIds!);
        Assert.Null(AnnouncementRules.Validate(request));
    }

    [Fact]
    public void Rules_NeedALabelAndALinkTogether_AndAnEndAfterTheStart()
    {
        Assert.Equal("A link needs a button label.",
            AnnouncementRules.Validate(AnnouncementRules.Normalize(Request() with { CtaLabel = " " })));
        Assert.Equal("A button label needs a link.",
            AnnouncementRules.Validate(AnnouncementRules.Normalize(Request() with { CtaUrl = null })));
        Assert.Equal("The end of the window must be after its start.",
            AnnouncementRules.Validate(AnnouncementRules.Normalize(Request() with { StartsAt = Now, EndsAt = Now })));
    }

    // ── CMS v2: frequency, targeting, events, analytics, bulk, assets, audit ────────────────

    private Announcement Live(Action<Announcement> configure)
    {
        var announcement = Seed(AnnouncementConstants.StatusPublished);
        configure(announcement);
        return announcement;
    }

    private void State(Announcement announcement, Action<AnnouncementViewerState> configure)
    {
        var state = new AnnouncementViewerState { AnnouncementId = announcement.Id, UserId = ViewerId };
        configure(state);
        _states.Add(state);
    }

    private async Task<HashSet<Guid>> VisibleIds(string? locale = null, string? session = "now") =>
        (await Service().GetActiveForViewerAsync(ViewerId, locale, session)).Value!.Select(a => a.Id).ToHashSet();

    [Fact]
    public async Task Frequency_Once_ShowsForTheRestOfTheSessionItWasSeenIn_ThenNeverAgain()
    {
        var once = Live(a => a.Frequency = AnnouncementConstants.FrequencyOnce);
        State(once, s => { s.ImpressionCount = 1; s.LastSessionId = "now"; });

        Assert.Contains(once.Id, await VisibleIds(session: "now"));
        Assert.DoesNotContain(once.Id, await VisibleIds(session: "next"));
    }

    [Fact]
    public async Task Frequency_Daily_ComesBackAfterADay()
    {
        var daily = Live(a => a.Frequency = AnnouncementConstants.FrequencyDaily);
        var yesterday = Live(a => a.Frequency = AnnouncementConstants.FrequencyDaily);
        State(daily, s => { s.ImpressionCount = 1; s.LastSeenAt = Now.AddHours(-2); s.LastSessionId = "earlier"; });
        State(yesterday, s => { s.ImpressionCount = 1; s.LastSeenAt = Now.AddHours(-25); s.LastSessionId = "earlier"; });

        var visible = await VisibleIds(session: "now");

        Assert.DoesNotContain(daily.Id, visible);
        Assert.Contains(yesterday.Id, visible);
    }

    [Fact]
    public async Task Frequency_EverySession_ReturnsAfterADismissal_InTheNextSession()
    {
        var everySession = Live(a => a.Frequency = AnnouncementConstants.FrequencyEverySession);
        var untilDismissed = Live(_ => { });
        State(everySession, s => { s.DismissedAt = Now.AddMinutes(-5); s.DismissedSessionId = "old"; });
        State(untilDismissed, s => { s.DismissedAt = Now.AddMinutes(-5); s.DismissedSessionId = "old"; });

        Assert.DoesNotContain(everySession.Id, await VisibleIds(session: "old"));
        var next = await VisibleIds(session: "new");
        Assert.Contains(everySession.Id, next);
        Assert.DoesNotContain(untilDismissed.Id, next);
    }

    [Fact]
    public async Task NonDismissible_IsShownEvenAfterAnOldDismissal_AndRefusesANewOne()
    {
        var pinned = Live(a => a.Dismissible = false);
        State(pinned, s => s.DismissedAt = Now.AddDays(-1));

        Assert.Contains(pinned.Id, await VisibleIds());
        Assert.Equal(ErrorCodes.InvalidState,
            (await Service().RecordEventAsync(ViewerId, pinned.Id, new AnnouncementEventRequest("DISMISS", "now"))).ErrorCode);
    }

    [Fact]
    public async Task Targeting_ByRoleLocaleAndAccountAge_AllMustMatch()
    {
        var admins = Live(a => a.TargetRoles = ["Owner", "Admin"]);
        var members = Live(a => a.TargetRoles = ["Member"]);
        var vietnamese = Live(a => a.TargetLocales = ["vi"]);
        var newcomers = Live(a => a.NewUsersWithinDays = 7);
        var veterans = Live(a => a.NewUsersWithinDays = 1);
        _audience.Setup(a => a.ResolveAsync(ViewerId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ViewerAudience([], [], ["member"], Now.AddDays(-3))));

        var visible = await VisibleIds(locale: "vi-VN");

        Assert.DoesNotContain(admins.Id, visible);
        Assert.Contains(members.Id, visible);
        Assert.Contains(vietnamese.Id, visible);
        Assert.Contains(newcomers.Id, visible);
        Assert.DoesNotContain(veterans.Id, visible);
        Assert.DoesNotContain(vietnamese.Id, await VisibleIds(locale: "en"));
    }

    [Fact]
    public async Task Impressions_CountOncePerSession_AndFeedTheAnalytics()
    {
        var live = Live(_ => { });
        var service = Service();

        await service.RecordEventAsync(ViewerId, live.Id, new AnnouncementEventRequest("IMPRESSION", "s1"));
        await service.RecordEventAsync(ViewerId, live.Id, new AnnouncementEventRequest("IMPRESSION", "s1"));
        await service.RecordEventAsync(ViewerId, live.Id, new AnnouncementEventRequest("IMPRESSION", "s2"));
        await service.RecordEventAsync(ViewerId, live.Id, new AnnouncementEventRequest("cta_click", "s2"));

        var state = Assert.Single(_states);
        Assert.Equal(2, state.ImpressionCount);
        Assert.Equal(1, state.CtaClickCount);

        var analytics = (await service.GetAnalyticsAsync(live.Id, 7)).Value!;
        Assert.Equal(1, analytics.Totals.UniqueViewers);
        Assert.Equal(2, analytics.Totals.Impressions);
        Assert.Equal(1.0, analytics.Totals.ClickThroughRate);
        Assert.Equal(7, analytics.Daily.Count);
        Assert.Equal(2, analytics.Daily[^1].Impressions);
    }

    [Fact]
    public async Task Events_OnlyCountForPublishedAnnouncements_AndOnlyKnownTypes()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft);
        var live = Live(_ => { });

        Assert.Equal(ErrorCodes.NotFound, (await Service().RecordEventAsync(ViewerId, draft.Id, new AnnouncementEventRequest("IMPRESSION"))).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, (await Service().RecordEventAsync(ViewerId, live.Id, new AnnouncementEventRequest("HOVER"))).ErrorCode);
    }

    [Fact]
    public async Task Bulk_ActsOnEachItem_AndReportsEachOutcome()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft);
        var scheduledDraft = Seed(AnnouncementConstants.StatusDraft, startsAt: Now.AddDays(1));
        var live = Seed(AnnouncementConstants.StatusPublished);

        var publish = (await Service().BulkAsync(Admin, new AnnouncementBulkRequest("publish", [draft.Id, scheduledDraft.Id, live.Id]))).Value!;
        var delete = (await Service().BulkAsync(Admin, new AnnouncementBulkRequest("delete", [draft.Id]))).Value!;

        Assert.Equal(new[] { true, true, false }, publish.Items.Select(i => i.Succeeded));
        Assert.Equal("SCHEDULED", AnnouncementLifecycle.EffectiveStatus(scheduledDraft, Now));
        Assert.False(Assert.Single(delete.Items).Succeeded); // now published: archive it instead
    }

    [Fact]
    public async Task Duplicate_KeepsTheDesignAndTargeting()
    {
        var source = Live(a =>
        {
            a.Placement = AnnouncementConstants.PlacementModal;
            a.AccentColor = "VIOLET";
            a.Icon = "rocket";
            a.TargetRoles = ["Owner"];
            a.Priority = 80;
            a.SecondaryCtaLabel = "Later";
            a.SecondaryCtaUrl = "/home";
        });

        var copy = (await Service().DuplicateAsync(Admin, source.Id)).Value!;

        Assert.Equal((AnnouncementConstants.PlacementModal, "VIOLET", "rocket", 80, "Later"),
            (copy.Placement, copy.AccentColor, copy.Icon, copy.Priority, copy.SecondaryCtaLabel));
        Assert.Equal(["Owner"], copy.TargetRoles);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, true)]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData("image/png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, false)]
    [InlineData("image/svg+xml", new byte[] { 0x3C, 0x73, 0x76, 0x67 }, false)]
    public async Task Assets_AcceptOnlyRealImagesOfTheClaimedType(string contentType, byte[] bytes, bool accepted)
    {
        var result = await Service().UploadAssetAsync(Admin, "hero.png", contentType, bytes);

        Assert.Equal(accepted, result.IsSuccess);
        if (accepted)
        {
            Assert.StartsWith(AnnouncementRules.AssetPathPrefix, result.Value!.Url);
            Assert.True(AnnouncementRules.IsAllowedImage(result.Value.Url));
            Assert.Equal(bytes, (await Service().GetAssetAsync(result.Value.Id)).Value!.Content);
        }
    }

    [Theory]
    [InlineData("BILLBOARD", "SUBTLE", "BRAND", null, "Placement must be one of")]
    [InlineData("MODAL", "NEON", "BRAND", null, "Style must be one of")]
    [InlineData("MODAL", "SOLID", "PINK", null, "Color must be one of")]
    [InlineData("MODAL", "SOLID", "BRAND", "unicorn", "is not an icon")]
    public void Rules_RefuseADesignTheAppCannotDraw(string placement, string variant, string color, string? icon, string expected)
    {
        var request = AnnouncementRules.Normalize(Request() with { Placement = placement, Variant = variant, AccentColor = color, Icon = icon });

        Assert.Contains(expected, AnnouncementRules.Validate(request));
    }

    [Fact]
    public void Rules_CheckTheSecondButtonTargetingAndImage()
    {
        string? Validate(UpsertAnnouncementRequest request) => AnnouncementRules.Validate(AnnouncementRules.Normalize(request));

        Assert.Equal("Add the main button before a second one.",
            Validate(Request() with { CtaLabel = null, CtaUrl = null, SecondaryCtaLabel = "Later", SecondaryCtaUrl = "/x" }));
        Assert.Contains("is not a workspace role", Validate(Request() with { TargetRoles = ["Superuser"] }));
        Assert.Contains("is not a language", Validate(Request() with { TargetLocales = ["fr"] }));
        Assert.Contains("between 1 and", Validate(Request() with { NewUsersWithinDays = 0 }));
        Assert.Equal("The image must be an uploaded image or an https:// address.", Validate(Request() with { ImageUrl = "http://x.test/a.png" }));
        Assert.Null(Validate(Request() with { ImageUrl = AnnouncementRules.AssetPathPrefix + Guid.NewGuid(), TargetRoles = ["owner"] }));
        Assert.Equal(["Owner"], AnnouncementRules.Normalize(Request() with { TargetRoles = ["owner"] }).TargetRoles);
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}

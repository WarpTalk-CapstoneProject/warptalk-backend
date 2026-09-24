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

namespace WarpTalk.NotificationService.Tests.Application.Announcements;

public sealed class AnnouncementServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid ViewerId = Guid.NewGuid();

    private readonly List<Announcement> _announcements = [];
    private readonly List<AnnouncementDismissal> _dismissals = [];
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

        var dismissals = new Mock<IAnnouncementDismissalRepository>();
        dismissals.Setup(r => r.GetDismissedIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                (IReadOnlySet<Guid>)_dismissals.Where(d => d.UserId == userId && ids.Contains(d.AnnouncementId)).Select(d => d.AnnouncementId).ToHashSet());
        dismissals.Setup(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, Guid userId, CancellationToken _) => _dismissals.Any(d => d.AnnouncementId == id && d.UserId == userId));
        dismissals.Setup(r => r.AddAsync(It.IsAny<AnnouncementDismissal>(), It.IsAny<CancellationToken>()))
            .Callback<AnnouncementDismissal, CancellationToken>((d, _) => _dismissals.Add(d))
            .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.AnnouncementRepository).Returns(repository.Object);
        _unitOfWork.Setup(u => u.AnnouncementDismissalRepository).Returns(dismissals.Object);
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
        var result = await Service().CreateAsync(AdminId, Request());

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

        var result = await Service().PublishAsync(AdminId, draft.Id, new PublishAnnouncementRequest());

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

        var past = await Service().PublishAsync(AdminId, draft.Id, new PublishAnnouncementRequest(Now.AddMinutes(-1)));
        var future = await Service().PublishAsync(AdminId, draft.Id, new PublishAnnouncementRequest(Now.AddHours(2)));

        Assert.Equal(ErrorCodes.ValidationError, past.ErrorCode);
        Assert.True(future.IsSuccess, future.Error);
        Assert.Equal("SCHEDULED", future.Value!.EffectiveStatus);
    }

    [Fact]
    public async Task Publish_WithAnEndAlreadyPassed_IsRefused()
    {
        var draft = Seed(AnnouncementConstants.StatusDraft, endsAt: Now.AddHours(-1));

        var result = await Service().PublishAsync(AdminId, draft.Id, new PublishAnnouncementRequest());

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(AnnouncementConstants.StatusDraft, draft.Status);
    }

    [Fact]
    public async Task Unpublish_Archive_AndDelete_FollowTheLifecycle()
    {
        var live = Seed(AnnouncementConstants.StatusPublished);
        var service = Service();

        Assert.Equal(ErrorCodes.InvalidState, (await service.DeleteAsync(live.Id)).ErrorCode);

        Assert.True((await service.ArchiveAsync(AdminId, live.Id)).IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, (await service.ArchiveAsync(AdminId, live.Id)).ErrorCode);
        Assert.Equal(ErrorCodes.InvalidState, (await service.UpdateAsync(AdminId, live.Id, Request())).ErrorCode);

        Assert.True((await service.UnpublishAsync(AdminId, live.Id)).IsSuccess);
        Assert.Equal(AnnouncementConstants.StatusDraft, live.Status);
        Assert.Equal(ErrorCodes.InvalidState, (await service.UnpublishAsync(AdminId, live.Id)).ErrorCode);

        Assert.True((await service.DeleteAsync(live.Id)).IsSuccess);
        Assert.Empty(_announcements);
    }

    [Fact]
    public async Task Duplicate_IsADraftCopyWithoutTheWindow()
    {
        var original = Seed(AnnouncementConstants.StatusPublished, startsAt: Now.AddDays(-1), endsAt: Now.AddDays(1),
            audience: AnnouncementConstants.AudiencePlans, plans: ["pro"]);

        var copy = (await Service().DuplicateAsync(AdminId, original.Id)).Value!;

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

        _audience.Setup(a => a.ResolveAsync(ViewerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ViewerAudience([workspaceId], ["PRO"])));

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
        _audience.Setup(a => a.ResolveAsync(ViewerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ViewerAudience>("down", ErrorCodes.ServiceUnavailable));

        var visible = (await Service().GetActiveForViewerAsync(ViewerId)).Value!;

        Assert.Equal(everyone.Id, Assert.Single(visible).Id);
    }

    [Fact]
    public async Task Viewer_WhoOnlyHasEveryoneAnnouncements_NeverCostsAWorkspaceLookup()
    {
        Seed(AnnouncementConstants.StatusPublished);

        await Service().GetActiveForViewerAsync(ViewerId);

        _audience.Verify(a => a.ResolveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dismissed_IsNotShownAgain_AndDismissingTwiceIsHarmless()
    {
        var first = Seed(AnnouncementConstants.StatusPublished);
        var second = Seed(AnnouncementConstants.StatusPublished);
        var service = Service();

        Assert.True((await service.DismissAsync(ViewerId, first.Id)).IsSuccess);
        Assert.True((await service.DismissAsync(ViewerId, first.Id)).IsSuccess);

        Assert.Single(_dismissals);
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

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}

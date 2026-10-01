using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-708: a recurring booking the workspace language policy refuses stops producing meetings
/// (WT-707) — and now the HOST is told, once per problem rather than once per sweep.
///
/// What these pin is the delivery bookkeeping, because that is where this goes wrong: a marker
/// that is never claimed spams the host every few minutes forever; a marker that is not released
/// on a failed send turns one NotificationService blip into a week of silence.
/// </summary>
public class SeriesLanguagePolicyBlockedNotificationTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 8, 13, 3, 0, 0, DateTimeKind.Utc);

    private readonly Mock<NotificationGrpcService.NotificationGrpcServiceClient> _notifications = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly List<SendNotificationRequest> _sent = new();

    private static TranslationRoomSeries RefusedSeries() => new()
    {
        Id = SeriesId,
        HostId = HostId,
        WorkspaceId = WorkspaceId,
        Status = RecurrenceSeriesStatuses.Active,
        RecurrenceType = "DAILY",
        RecurrenceInterval = 1,
        TimeZone = "Asia/Ho_Chi_Minh",
        StartTimeLocal = new TimeOnly(9, 0),
        StartsOnLocalDate = new DateOnly(2026, 8, 10),
        EndsOnLocalDate = new DateOnly(2026, 8, 20),
        MaterializedThroughLocalDate = new DateOnly(2026, 8, 12),
        Title = "Daily sync",
        SourceLanguage = "vi",
        TargetLanguages = "[\"es\"]",
        TranslationRoomType = "MEETING",
        MaxParticipants = 10,
    };

    private TranslationRoomSeriesService CreateService(TranslationRoomSeries series, bool withRedis = true)
    {
        var seriesRepo = new Mock<ITranslationRoomSeriesRepository>();
        seriesRepo
            .Setup(r => r.GetSeriesNeedingMaterializationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomSeries> { series });

        var roomRepo = new Mock<ITranslationRoomRepository>();
        roomRepo
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<TranslationRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoom>());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.TranslationRoomSeriesRepository).Returns(seriesRepo.Object);
        unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepo.Object);

        var meetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
        meetingPolicy
            .Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("Language 'es' is not allowed in this workspace.", ErrorCodes.ValidationError));

        var languagePolicy = new Mock<ILanguagePolicy>();
        languagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);

        return new TranslationRoomSeriesService(
            unitOfWork.Object,
            Mock.Of<ITranslationRoomService>(),
            meetingPolicy.Object,
            languagePolicy.Object,
            NullLogger<TranslationRoomSeriesService>.Instance,
            () => Now,
            _notifications.Object,
            withRedis ? _redis.Object : null);
    }

    private void SendSucceeds() =>
        _notifications
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<SendNotificationRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((SendNotificationRequest r, Metadata _, DateTime? _, CancellationToken _) => _sent.Add(r))
            .Returns(new AsyncUnaryCall<SendNotificationResponse>(
                Task.FromResult(new SendNotificationResponse()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

    private void MarkerClaimable(bool claimable) =>
        _redis
            .Setup(r => r.StringSetIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(claimable);

    [Fact]
    public async Task A_refused_template_tells_the_host_naming_the_series()
    {
        SendSucceeds();
        MarkerClaimable(true);

        await CreateService(RefusedSeries()).MaterializeDueOccurrencesAsync();

        _sent.Should().ContainSingle();
        var sent = _sent[0];
        sent.UserId.Should().Be(HostId.ToString());
        sent.Type.Should().Be("MEETING_SERIES_BLOCKED");
        sent.Metadata["series_id"].Should().Be(SeriesId.ToString());
        sent.Metadata["series_title"].Should().Be("Daily sync");
        sent.Body.Should().Contain("'es' is not allowed");

        _redis.Verify(r => r.StringSetIfAbsentAsync(
            $"series:{SeriesId}:language_policy_alert", It.IsAny<string>(), TimeSpan.FromDays(7)), Times.Once);
    }

    [Fact]
    public async Task A_later_sweep_that_finds_the_marker_already_claimed_stays_silent()
    {
        SendSucceeds();
        MarkerClaimable(false);

        await CreateService(RefusedSeries()).MaterializeDueOccurrencesAsync();

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_releases_the_marker_so_the_next_sweep_retries()
    {
        MarkerClaimable(true);
        _notifications
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<SendNotificationRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));

        var created = await CreateService(RefusedSeries()).MaterializeDueOccurrencesAsync();

        created.Should().Be(0);
        _redis.Verify(r => r.KeyDeleteAsync($"series:{SeriesId}:language_policy_alert"), Times.Once);
    }

    [Fact]
    public async Task Without_Redis_nothing_is_sent_rather_than_one_notice_per_sweep()
    {
        SendSucceeds();

        await CreateService(RefusedSeries(), withRedis: false).MaterializeDueOccurrencesAsync();

        _sent.Should().BeEmpty();
    }
}

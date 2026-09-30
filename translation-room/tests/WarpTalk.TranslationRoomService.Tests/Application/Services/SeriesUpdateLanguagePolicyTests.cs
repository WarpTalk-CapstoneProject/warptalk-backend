using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-707: a series edit is held to the same language rules as a one-off room edit, and a
/// template the workspace refuses is never handed to the materialisation sweep.
///
/// Before this, PATCH /translation-room-series/{id} wrote the requested languages straight onto
/// the template, let each occurrence's own update refuse them (refusals that were only logged),
/// and answered 200. The template then held e.g. ["es"] outside the workspace whitelist, or [],
/// and every sweep failed to create the next occurrence from it — forever, silently.
/// </summary>
public class SeriesUpdateLanguagePolicyTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    /// <summary>Now, in UTC. Asia/Ho_Chi_Minh is UTC+7, so the local date is 2026-08-13.</summary>
    private static readonly DateTime Now = new(2026, 8, 13, 3, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Watermark = new(2026, 8, 12);

    private static TranslationRoomSeries DailySeries() => new()
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
        MaterializedThroughLocalDate = Watermark,
        Title = "Daily meeting test",
        SourceLanguage = "vi",
        TargetLanguages = "[\"en\"]",
        TranslationRoomType = "MEETING",
        MaxParticipants = 10,
    };

    private sealed record Harness(
        TranslationRoomSeriesService Service,
        Mock<ITranslationRoomService> RoomService,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IWorkspaceMeetingPolicy> MeetingPolicy,
        Mock<ILanguagePolicy> LanguagePolicy);

    private static Harness CreateService(TranslationRoomSeries series, IReadOnlyList<TranslationRoom>? futureOccurrences = null)
    {
        var seriesRepo = new Mock<ITranslationRoomSeriesRepository>();
        seriesRepo.Setup(r => r.GetByIdAsync(series.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);
        seriesRepo
            .Setup(r => r.GetSeriesNeedingMaterializationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomSeries> { series });
        seriesRepo
            .Setup(r => r.GetCancellableOccurrencesAsync(series.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(futureOccurrences ?? new List<TranslationRoom>());

        var roomRepo = new Mock<ITranslationRoomRepository>();
        roomRepo
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoom>());
        roomRepo
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.TranslationRoomSeriesRepository).Returns(seriesRepo.Object);
        unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepo.Object);

        var roomService = new Mock<ITranslationRoomService>();
        roomService
            .Setup(s => s.UpdateTranslationRoomSettingsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateRoomSettingsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        roomService
            .Setup(s => s.CreateTranslationRoomAsync(
                It.IsAny<CreateTranslationRoomRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<SeriesOccurrenceContext>()))
            .ReturnsAsync(Result.Success<TranslationRoomDto>(null!));

        // Default: the workspace allows every language and the platform supports every code.
        // Each test narrows exactly the rule it is about.
        var meetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
        meetingPolicy
            .Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var languagePolicy = new Mock<ILanguagePolicy>();
        languagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);

        var service = new TranslationRoomSeriesService(
            unitOfWork.Object,
            roomService.Object,
            meetingPolicy.Object,
            languagePolicy.Object,
            NullLogger<TranslationRoomSeriesService>.Instance,
            () => Now);

        return new Harness(service, roomService, unitOfWork, meetingPolicy, languagePolicy);
    }

    private static void RefuseWorkspaceLanguages(Mock<IWorkspaceMeetingPolicy> meetingPolicy, string errorCode) =>
        meetingPolicy
            .Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("Language 'es' is not allowed in this workspace.", errorCode));

    private static void VerifyNothingWritten(Harness h)
    {
        h.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "a refused edit must leave the booking exactly as it was");
        h.RoomService.Verify(
            s => s.UpdateTranslationRoomSettingsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateRoomSettingsRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no occurrence may be touched by an edit the booking itself refused");
    }

    private static List<TranslationRoom> OneFutureOccurrence() =>
        new() { new TranslationRoom { Id = Guid.NewGuid(), SeriesId = SeriesId } };

    [Fact]
    public async Task Patch_WithALanguageTheWorkspaceRefuses_IsRejectedAndNothingIsSaved()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());
        RefuseWorkspaceLanguages(h.MeetingPolicy, ErrorCodes.ValidationError);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(TargetLanguages: new List<string> { "es" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        series.TargetLanguages.Should().Be("[\"en\"]");
        VerifyNothingWritten(h);
    }

    [Fact]
    public async Task Patch_WithAnEmptyTargetList_IsRejected()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(TargetLanguages: new List<string> { " ", "" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(TranslationRoomConstants.ValidationTargetLanguagesRequired);
        series.TargetLanguages.Should().Be("[\"en\"]");
        VerifyNothingWritten(h);
    }

    [Fact]
    public async Task Patch_WithALanguageThePlatformDoesNotSupport_IsRejected()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());
        h.LanguagePolicy.Setup(p => p.IsSupportedAsync("xx")).ReturnsAsync(false);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(TargetLanguages: new List<string> { "en", "xx" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, "xx"));
        VerifyNothingWritten(h);
        h.MeetingPolicy.Verify(
            p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Patch_WithAnUnsupportedSourceLanguage_IsRejected()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());
        h.LanguagePolicy.Setup(p => p.IsSupportedAsync("xx")).ReturnsAsync(false);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(SourceLanguage: "xx"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(TranslationRoomConstants.ValidationSourceLanguageUnsupported);
        series.SourceLanguage.Should().Be("vi");
        VerifyNothingWritten(h);
    }

    [Fact]
    public async Task Patch_StoresNormalisedLanguages_OnTheTemplateAndPassesThemToOccurrences()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId,
            new UpdateSeriesRequest(SourceLanguage: "vi-VN", TargetLanguages: new List<string> { "en-US", "EN", "ja-JP" }));

        result.IsSuccess.Should().BeTrue();
        series.SourceLanguage.Should().Be("vi");
        LanguageHelper.ParseTargetLanguages(series.TargetLanguages).Should().Equal("en", "ja");

        h.MeetingPolicy.Verify(
            p => p.ValidateRoomLanguagesAsync(
                WorkspaceId,
                "vi",
                It.Is<IEnumerable<string>>(t => t.SequenceEqual(new[] { "en", "ja" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
        h.RoomService.Verify(
            s => s.UpdateTranslationRoomSettingsAsync(
                It.IsAny<Guid>(), HostId,
                It.Is<UpdateRoomSettingsRequest>(r =>
                    r.SourceLanguage == "vi" && r.TargetLanguages!.SequenceEqual(new[] { "en", "ja" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Patch_ChangingOnlyTheSource_IsJudgedAlongsideTheTargetsTheBookingKeeps()
    {
        var series = DailySeries();
        var h = CreateService(series);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(SourceLanguage: "ja-JP"));

        result.IsSuccess.Should().BeTrue();
        h.MeetingPolicy.Verify(
            p => p.ValidateRoomLanguagesAsync(
                WorkspaceId,
                "ja",
                It.Is<IEnumerable<string>>(t => t.SequenceEqual(new[] { "en" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Patch_WhenTheWorkspaceServiceIsUnavailable_PassesThatThroughAndSavesNothing()
    {
        var series = DailySeries();
        var h = CreateService(series, OneFutureOccurrence());
        RefuseWorkspaceLanguages(h.MeetingPolicy, ErrorCodes.ServiceUnavailable);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(TargetLanguages: new List<string> { "ja" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ServiceUnavailable);
        VerifyNothingWritten(h);
    }

    [Fact]
    public async Task Patch_WithoutLanguages_DoesNotConsultTheWorkspacePolicy()
    {
        var series = DailySeries();
        var h = CreateService(series);

        var result = await h.Service.UpdateSeriesAsync(
            SeriesId, HostId, new UpdateSeriesRequest(Title: "Renamed"));

        result.IsSuccess.Should().BeTrue();
        series.Title.Should().Be("Renamed");
        h.MeetingPolicy.Verify(
            p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Materialise_WithATemplateTheWorkspaceRefuses_CreatesNothingAndKeepsTheWatermark()
    {
        var series = DailySeries();
        series.TargetLanguages = "[\"es\"]";
        var h = CreateService(series);
        RefuseWorkspaceLanguages(h.MeetingPolicy, ErrorCodes.ValidationError);

        var created = await h.Service.MaterializeDueOccurrencesAsync();

        created.Should().Be(0);
        series.MaterializedThroughLocalDate.Should().Be(Watermark,
            "skipping the dates would silently delete meetings the host booked");
        series.Status.Should().Be(RecurrenceSeriesStatuses.Active);
        h.RoomService.Verify(
            s => s.CreateTranslationRoomAsync(
                It.IsAny<CreateTranslationRoomRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<SeriesOccurrenceContext>()),
            Times.Never);
        h.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Materialise_WithAnAllowedTemplate_StillCreatesOccurrences()
    {
        var series = DailySeries();
        var h = CreateService(series);

        var created = await h.Service.MaterializeDueOccurrencesAsync();

        created.Should().BeGreaterThan(0);
        h.MeetingPolicy.Verify(
            p => p.ValidateRoomLanguagesAsync(
                WorkspaceId,
                "vi",
                It.Is<IEnumerable<string>>(t => t.SequenceEqual(new[] { "en" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

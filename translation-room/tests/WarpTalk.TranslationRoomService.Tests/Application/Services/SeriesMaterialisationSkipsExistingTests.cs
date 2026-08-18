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
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// The materialisation sweep must never be handed the same work twice forever.
///
/// Production ran this loop every five minutes for hours:
///
///     23505: duplicate key value violates unique constraint
///            "translation_rooms_series_id_occurrence_date_key"
///     DETAIL: Key (series_id, series_occurrence_local_date)=(019fe933…, 2026-08-25) already exists.
///     WT-327: series 019fe933… could not materialise 08/25/2026; stopping this pass.
///
/// Two things had to be true at once for that to repeat forever. The enumerator works from the
/// watermark and knows nothing about the rows, while the unique index counts every row INCLUDING
/// cancelled ones — so a series whose occurrences were cancelled in bulk is offered dates the
/// database will refuse. And the pass only persisted anything when it had CREATED something, so
/// a pass that failed on its first date saved nothing at all and the next pass got the identical
/// list.
///
/// Both series in production were ACTIVE dailies whose every occurrence was CANCELLED — the same
/// state SeriesCancelOrderingTests exists for. This is what the sweep should do when it meets it.
/// </summary>
public class SeriesMaterialisationSkipsExistingTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    /// <summary>Now, in UTC. Asia/Ho_Chi_Minh is UTC+7, so the local date is 2026-08-13.</summary>
    private static readonly DateTime Now = new(2026, 8, 13, 3, 0, 0, DateTimeKind.Utc);

    private static TranslationRoomSeries DailySeries(DateOnly materializedThrough) => new()
    {
        Id = SeriesId,
        HostId = HostId,
        WorkspaceId = Guid.NewGuid(),
        Status = RecurrenceSeriesStatuses.Active,
        RecurrenceType = "DAILY",
        RecurrenceInterval = 1,
        TimeZone = "Asia/Ho_Chi_Minh",
        StartTimeLocal = new TimeOnly(9, 0),
        StartsOnLocalDate = new DateOnly(2026, 8, 10),
        EndsOnLocalDate = new DateOnly(2026, 8, 20),
        MaterializedThroughLocalDate = materializedThrough,
        Title = "Daily meeting test",
        SourceLanguage = "vi-VN",
        TargetLanguages = "[\"en-US\"]",
        TranslationRoomType = "MEETING",
        MaxParticipants = 10,
    };

    private static TranslationRoom Occurrence(DateOnly date, string status) => new()
    {
        Id = Guid.NewGuid(),
        SeriesId = SeriesId,
        SeriesOccurrenceLocalDate = date,
        Status = status,
        TranslationRoomCode = "abc-defg-hij",
    };

    private static (TranslationRoomSeriesService Service,
                    Mock<ITranslationRoomService> RoomService,
                    Mock<IUnitOfWork> UnitOfWork)
        CreateService(TranslationRoomSeries series, List<TranslationRoom> existingOccurrences)
    {
        var seriesRepo = new Mock<ITranslationRoomSeriesRepository>();
        seriesRepo
            .Setup(r => r.GetSeriesNeedingMaterializationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomSeries> { series });

        var roomRepo = new Mock<ITranslationRoomRepository>();
        roomRepo
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TranslationRoom, bool>> predicate, string _, CancellationToken __) =>
                existingOccurrences.Where(predicate.Compile()).ToList());
        roomRepo
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TranslationRoom, bool>> predicate, string _, CancellationToken __) =>
                existingOccurrences.FirstOrDefault(predicate.Compile()));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.TranslationRoomSeriesRepository).Returns(seriesRepo.Object);
        unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepo.Object);

        var roomService = new Mock<ITranslationRoomService>();

        var service = new TranslationRoomSeriesService(
            unitOfWork.Object,
            roomService.Object,
            NullLogger<TranslationRoomSeriesService>.Instance,
            () => Now);

        return (service, roomService, unitOfWork);
    }

    private static void StubSuccessfulCreate(Mock<ITranslationRoomService> roomService) =>
        roomService
            .Setup(s => s.CreateTranslationRoomAsync(
                It.IsAny<CreateTranslationRoomRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<SeriesOccurrenceContext>()))
            .ReturnsAsync(Result.Success<TranslationRoomDto>(null!));

    /// <summary>
    /// The regression. Every date the sweep is about to offer already has a CANCELLED room, so
    /// creating any of them would be the 23505 seen in production.
    /// </summary>
    [Fact]
    public async Task ASeriesWhoseOccurrencesWereAllCancelled_DoesNotTryToCreateThemAgain()
    {
        var series = DailySeries(materializedThrough: new DateOnly(2026, 8, 12));
        var existing = new List<TranslationRoom>
        {
            Occurrence(new DateOnly(2026, 8, 13), "CANCELLED"),
            Occurrence(new DateOnly(2026, 8, 14), "CANCELLED"),
            Occurrence(new DateOnly(2026, 8, 15), "CANCELLED"),
        };
        var (service, roomService, _) = CreateService(series, existing);
        StubSuccessfulCreate(roomService);

        await service.MaterializeDueOccurrencesAsync();

        // Not one of them. Every such attempt is the 23505 from production, and the first one
        // used to abort the pass before anything was saved.
        foreach (var taken in new[] { new DateOnly(2026, 8, 13), new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 15) })
        {
            roomService.Verify(
                s => s.CreateTranslationRoomAsync(
                    It.IsAny<CreateTranslationRoomRequest>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>(),
                    It.Is<SeriesOccurrenceContext>(c => c.LocalDate == taken)),
                Times.Never,
                $"{taken} already has a room, so creating it can only violate the unique index");
        }
    }

    /// <summary>
    /// The half that makes it a LOOP rather than a slow pass. The watermark has to be saved even
    /// when nothing was created, or the next sweep is handed the identical list.
    /// </summary>
    [Fact]
    public async Task TheWatermarkMovesPastOccurrencesThatAlreadyExist()
    {
        var series = DailySeries(materializedThrough: new DateOnly(2026, 8, 12));
        var existing = new List<TranslationRoom>
        {
            Occurrence(new DateOnly(2026, 8, 13), "CANCELLED"),
            Occurrence(new DateOnly(2026, 8, 14), "CANCELLED"),
        };
        var (service, roomService, unitOfWork) = CreateService(series, existing);
        StubSuccessfulCreate(roomService);

        await service.MaterializeDueOccurrencesAsync();

        series.MaterializedThroughLocalDate
            .Should().BeOnOrAfter(new DateOnly(2026, 8, 14),
                "a date whose room already exists is materialised, whatever its status");
        unitOfWork.Verify(
            u => u.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "a watermark that is not persisted is a sweep that repeats itself forever");
    }

    /// <summary>
    /// The behaviour that must NOT change: a date with no room is still created, and a genuine
    /// refusal still stops the pass rather than skipping a meeting the host booked.
    /// </summary>
    [Fact]
    public async Task ADateWithNoRoomIsStillCreated()
    {
        var series = DailySeries(materializedThrough: new DateOnly(2026, 8, 12));
        var existing = new List<TranslationRoom>
        {
            Occurrence(new DateOnly(2026, 8, 13), "CANCELLED"),
        };
        var (service, roomService, _) = CreateService(series, existing);
        StubSuccessfulCreate(roomService);

        var created = await service.MaterializeDueOccurrencesAsync();

        created.Should().BeGreaterThan(0, "08-14 onward have no room and are still due");
        roomService.Verify(
            s => s.CreateTranslationRoomAsync(
                It.IsAny<CreateTranslationRoomRequest>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>(),
                It.Is<SeriesOccurrenceContext>(c => c.LocalDate == new DateOnly(2026, 8, 13))),
            Times.Never,
            "the one date that already has a room is the one date not to create");
    }
}

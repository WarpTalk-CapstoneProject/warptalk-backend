using System;
using System.Collections.Generic;
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
/// Stopping a recurring booking must survive the request being cancelled halfway through.
///
/// Production held this state, and nothing in the system has a name for it:
///
///     series      "Daily meeting test"   ACTIVE
///     occurrences 14 of 14               CANCELLED, all within 80ms
///
/// A booking that says it is running, every meeting of which is cancelled. The cause was
/// ordering: the series status was the LAST write in CancelSeriesAsync, after a loop that cancels
/// each future occurrence — and each of those commits as it goes, while the CancellationToken the
/// method receives is the ASP.NET REQUEST token. A host who closed the tab, navigated away, or met
/// a gateway timeout partway through cancelled every meeting and stopped nothing.
/// </summary>
public class SeriesCancelOrderingTests
{
    private static TranslationRoomSeries ActiveSeries(Guid seriesId, Guid hostId) => new()
    {
        Id = seriesId,
        HostId = hostId,
        Status = RecurrenceSeriesStatuses.Active,
    };

    private static (TranslationRoomSeriesService Service,
                    Mock<ITranslationRoomSeriesRepository> SeriesRepo,
                    Mock<ITranslationRoomService> RoomService,
                    Mock<IUnitOfWork> UnitOfWork) CreateService(TranslationRoomSeries series)
    {
        var seriesRepo = new Mock<ITranslationRoomSeriesRepository>();
        seriesRepo.Setup(r => r.GetByIdAsync(series.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.TranslationRoomSeriesRepository).Returns(seriesRepo.Object);

        var roomService = new Mock<ITranslationRoomService>();

        var service = new TranslationRoomSeriesService(
            unitOfWork.Object,
            roomService.Object,
            NullLogger<TranslationRoomSeriesService>.Instance,
            () => new DateTime(2026, 8, 12, 4, 45, 0, DateTimeKind.Utc));

        return (service, seriesRepo, roomService, unitOfWork);
    }

    /// <summary>
    /// The regression itself: the request dies while the occurrences are being cancelled. The
    /// booking must already be stopped by then, because stopping it is what was asked for.
    /// </summary>
    [Fact]
    public async Task CancelSeriesAsync_ShouldStopTheBooking_EvenWhenTheRequestDiesMidLoop()
    {
        var seriesId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var series = ActiveSeries(seriesId, hostId);
        var (service, seriesRepo, roomService, _) = CreateService(series);

        var first = new TranslationRoom { Id = Guid.NewGuid() };
        var second = new TranslationRoom { Id = Guid.NewGuid() };
        seriesRepo
            .Setup(r => r.GetCancellableOccurrencesAsync(seriesId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoom> { first, second });

        // The first occurrence cancels; the client then disconnects, which is what an aborted
        // request looks like from inside the loop.
        roomService
            .Setup(s => s.CancelTranslationRoomAsync(first.Id, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<TranslationRoomDto>(null!));
        roomService
            .Setup(s => s.CancelTranslationRoomAsync(second.Id, hostId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await service.CancelSeriesAsync(seriesId, hostId);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The point of the whole change: whatever happened to the occurrences, the booking is
        // stopped. Before the fix this was still Active, and the caller had cancelled a meeting
        // out of a series that would go on scheduling more.
        series.Status.Should().Be(RecurrenceSeriesStatuses.Cancelled);
        series.UpdatedBy.Should().Be(hostId);
    }

    /// <summary>
    /// The reason this is ordering and not a transaction: an occurrence that is already running
    /// refuses to cancel, and that must not prevent the host from stopping the booking — the
    /// moment a meeting is in progress is exactly when they most want to stop the rest.
    /// </summary>
    [Fact]
    public async Task CancelSeriesAsync_ShouldStopTheBooking_WhenAnOccurrenceRefusesToCancel()
    {
        var seriesId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var series = ActiveSeries(seriesId, hostId);
        var (service, seriesRepo, roomService, _) = CreateService(series);

        var running = new TranslationRoom { Id = Guid.NewGuid() };
        var later = new TranslationRoom { Id = Guid.NewGuid() };
        seriesRepo
            .Setup(r => r.GetCancellableOccurrencesAsync(seriesId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoom> { running, later });

        roomService
            .Setup(s => s.CancelTranslationRoomAsync(running.Id, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<TranslationRoomDto>(
                "Only scheduled or waiting rooms can be cancelled.", ErrorCodes.InvalidState));
        roomService
            .Setup(s => s.CancelTranslationRoomAsync(later.Id, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<TranslationRoomDto>(null!));

        var result = await service.CancelSeriesAsync(seriesId, hostId);

        result.IsSuccess.Should().BeTrue();
        series.Status.Should().Be(RecurrenceSeriesStatuses.Cancelled);
        // One refused, one cancelled — and the loop did not stop at the refusal.
        result.Value!.CancelledOccurrenceCount.Should().Be(1);
    }
}

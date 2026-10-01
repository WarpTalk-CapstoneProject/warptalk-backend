using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// The admin workspace page reads "meetings held" and "hours" for ONE workspace from the same
/// insights maths the platform page uses. The scope has to reach the query — a workspace page that
/// quietly shows the whole platform's hours is the failure this pins.
/// </summary>
public class AdminMeetingInsightsWorkspaceScopeTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public AdminMeetingInsightsWorkspaceScopeTests()
    {
        _unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _rooms
            .Setup(r => r.GetAdminCountsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0));
    }

    [Fact]
    public async Task A_workspace_id_scopes_the_meeting_spans_query()
    {
        var workspaceId = Guid.NewGuid();
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _rooms
            .Setup(r => r.GetAdminMeetingSpansAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), workspaceId))
            .ReturnsAsync(new List<AdminMeetingSpan>
            {
                new(from.AddHours(2), from.AddHours(4), 7200, "ENDED"),
            });
        _rooms
            .Setup(r => r.GetAdminMeetingSpansAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new List<AdminMeetingSpan>
            {
                new(from.AddHours(2), from.AddHours(4), 7200, "ENDED"),
                new(from.AddHours(5), from.AddHours(9), 14400, "ENDED"),
            });

        var service = new AdminMeetingService(_unitOfWork.Object, NullLogger<AdminMeetingService>.Instance);

        var result = await service.GetInsightsAsync(
            new AdminInsightsQuery { From = from, To = from.AddDays(1), Tz = "UTC" },
            CancellationToken.None,
            workspaceId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Metrics.Single(m => m.Id == "meetingsHeld").Value.Should().Be(1m);
        result.Value.Metrics.Single(m => m.Id == "hoursTranslated").Value.Should().Be(2m);
        _rooms.Verify(r => r.GetAdminMeetingSpansAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), workspaceId), Times.Once);
    }
}

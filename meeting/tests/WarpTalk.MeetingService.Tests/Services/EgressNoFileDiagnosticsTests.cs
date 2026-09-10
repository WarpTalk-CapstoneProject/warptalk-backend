using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-660. When an egress ends with no file, say which of the several very different reasons it
/// was.
///
/// Production spent an investigation stuck here. Every recording was ending as
/// <c>EgressCompletionOutcome.Cleared</c> and the only line written about it was
/// "finished with no recording file", which is true of all of these at once:
///
///   * EGRESS_COMPLETE with an empty file list — LiveKit did its job on something unrecordable.
///   * EGRESS_FAILED / EGRESS_ABORTED — LiveKit broke, and `error` says how.
///   * EGRESS_LIMIT_REACHED — the plan's recording minutes ran out mid-meeting.
///
/// Each needs a different response and the log could not tell them apart, because neither
/// LiveKit's `status` nor its `error` was ever read. These pin that both are now reported, in
/// both the string and the numeric form of the enum, and that the outcome itself is unchanged.
/// </summary>
public class EgressNoFileDiagnosticsTests
{
    [Fact]
    public async Task NoFile_ReportsLiveKitStatusAndError()
    {
        var (sut, logger) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            """{"egressId":"EG_x","roomName":"room-1","status":"EGRESS_FAILED","error":"template did not load"}"""));

        Assert.Equal(EgressCompletionOutcome.Cleared, outcome);
        AssertWarned(logger, "EGRESS_FAILED");
        AssertWarned(logger, "template did not load");
    }

    /// <summary>
    /// Twirp serialises a proto enum as its name, but the numeric form turns up too — the same
    /// split `EgressReconciliationService.IsTerminal` already handles. "6" in a log line tells a
    /// reader nothing; the reason this mapping exists is that "EGRESS_LIMIT_REACHED" tells them to
    /// go and look at the plan's minutes.
    /// </summary>
    [Fact]
    public async Task NoFile_NamesTheStatus_WhenLiveKitSendsTheOrdinal()
    {
        var (sut, logger) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            """{"egressId":"EG_x","roomName":"room-1","status":6}"""));

        Assert.Equal(EgressCompletionOutcome.Cleared, outcome);
        AssertWarned(logger, "EGRESS_LIMIT_REACHED");
    }

    /// <summary>
    /// The proto says "no error" with an empty string. Logging that verbatim reads as a truncated
    /// message, which sends a reader looking for the rest of it.
    /// </summary>
    [Fact]
    public async Task NoFile_SaysNone_RatherThanLoggingAnEmptyError()
    {
        var (sut, logger) = Build();

        await sut.ApplyAsync(EgressInfo(
            """{"egressId":"EG_x","roomName":"room-1","status":"EGRESS_COMPLETE","error":""}"""));

        AssertWarned(logger, "error=(none)");
    }

    /// <summary>
    /// A recording that DID produce a file must stay silent here — this warning is the signal that
    /// something needs looking at, and one per successful recording would retire it.
    /// </summary>
    [Fact]
    public async Task AFileMeansNoWarning()
    {
        var (sut, logger) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            """
            {"egressId":"EG_x","roomName":"room-1","status":"EGRESS_COMPLETE",
             "fileResults":[{"location":"s3://warptalk-recordings/room-1.mp4","size":1024}]}
            """));

        Assert.Equal(EgressCompletionOutcome.Published, outcome);
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private static JsonElement EgressInfo(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertWarned(Mock<ILogger<EgressCompletion>> logger, string expected) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(expected)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            $"expected the no-file warning to carry \"{expected}\"");

    private static (EgressCompletion Sut, Mock<ILogger<EgressCompletion>> Logger) Build()
    {
        var room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = Guid.NewGuid(),
            ProviderRoomName = "room-1",
            ActiveEgressId = "EG_x",
        };

        var roomRepository = new Mock<IMeetingRoomRepository>();
        roomRepository
            .Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                new[] { room }.FirstOrDefault(predicate.Compile()));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.MeetingRoomRepository).Returns(roomRepository.Object);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var redis = new Mock<IRedisService>();
        redis
            .Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success(true));

        var logger = new Mock<ILogger<EgressCompletion>>();

        return (new EgressCompletion(unitOfWork.Object, redis.Object, logger.Object), logger);
    }
}

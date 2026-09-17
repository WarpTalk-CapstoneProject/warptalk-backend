using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// rec-loss. A recording that STARTED must end in a visible state.
///
/// Before this, an egress that ended with no file — failed, aborted, out of minutes, or complete
/// with nothing uploaded — cleared the room and wrote one warning to the log. No event, so
/// translation-room never created a row, and the record page showed the same nothing it shows for
/// a meeting nobody recorded. These pin that EgressCompletion now publishes RecordingFailed with a
/// host-safe reason, and that the file path is untouched.
/// </summary>
public sealed class RecordingFailedEventTests
{
    [Theory]
    [InlineData("EGRESS_LIMIT_REACHED", "The recording stopped because the workspace ran out of recording minutes.")]
    [InlineData("EGRESS_FAILED", "The recording failed and no file was saved.")]
    [InlineData("EGRESS_ABORTED", "The recording failed and no file was saved.")]
    [InlineData("EGRESS_COMPLETE", "The recording finished but produced no file.")]
    public async Task NoFile_PublishesRecordingFailed_WithAReasonForTheHost(string status, string expectedReason)
    {
        var (sut, room, published) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            $$"""{"egressId":"EG_x","roomName":"room-1","status":"{{status}}","error":"upload to s3://secret-bucket/key failed","fileResults":[]}"""));

        Assert.Equal(EgressCompletionOutcome.Cleared, outcome);
        Assert.Null(room.ActiveEgressId);
        var fields = Assert.Single(published);
        Assert.Equal(MeetingEventTypes.RecordingFailed, fields["event_type"]);
        Assert.Equal(DomainEventEnvelope.CurrentSchemaVersion.ToString(), fields["schema_version"]);

        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingFailedEventPayload>>(fields["envelope"])!;
        Assert.Equal(fields["event_id"], envelope.EventId.ToString());
        Assert.Equal("meeting-service", envelope.Producer);
        Assert.Equal(room.TranslationRoomId, envelope.Payload.TranslationRoomId);
        Assert.Equal("EG_x", envelope.Payload.EgressId);
        Assert.Equal(expectedReason, envelope.Payload.Reason);
        // LiveKit's own words travel for operators, and never leak into the host-facing sentence.
        Assert.Equal(status, envelope.Payload.LiveKitStatus);
        Assert.Equal("upload to s3://secret-bucket/key failed", envelope.Payload.LiveKitError);
        Assert.DoesNotContain("s3://", envelope.Payload.Reason);
    }

    [Fact]
    public async Task NoFile_NumericLimitReached_StillGetsTheMinutesReason()
    {
        var (sut, _, published) = Build();

        await sut.ApplyAsync(EgressInfo("""{"egressId":"EG_x","roomName":"room-1","status":6}"""));

        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingFailedEventPayload>>(
            Assert.Single(published)["envelope"])!;
        Assert.Equal("EGRESS_LIMIT_REACHED", envelope.Payload.LiveKitStatus);
        Assert.Contains("recording minutes", envelope.Payload.Reason);
        Assert.Null(envelope.Payload.LiveKitError);
    }

    [Fact]
    public async Task NoFile_WithoutAnEgressId_PublishesNothing()
    {
        // The event is keyed by egress id; one without it could never resolve its Started.
        var (sut, room, published) = Build();
        room.ActiveEgressId = null;

        var outcome = await sut.ApplyAsync(EgressInfo("""{"roomName":"room-1","status":"EGRESS_FAILED"}"""));

        Assert.Equal(EgressCompletionOutcome.Cleared, outcome);
        Assert.Empty(published);
    }

    [Fact]
    public async Task NoFile_ForAnUnknownRoom_PublishesNothing()
    {
        var (sut, _, published) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            """{"egressId":"EG_other","roomName":"room-other","status":"EGRESS_FAILED"}"""));

        Assert.Equal(EgressCompletionOutcome.RoomNotFound, outcome);
        Assert.Empty(published);
    }

    [Fact]
    public async Task NoFile_Throws_AndKeepsTheRoomHoldingItsEgress_WhenThePublishFails()
    {
        // Same contract as Completed: the webhook turns the throw into a 500 so LiveKit retries,
        // and the sweep retries on its next tick — which it can only do if the id is still there.
        var (sut, room, _) = Build(publishSucceeds: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ApplyAsync(EgressInfo(
            """{"egressId":"EG_x","roomName":"room-1","status":"EGRESS_FAILED"}""")));

        Assert.Equal("EG_x", room.ActiveEgressId);
    }

    [Fact]
    public async Task AFile_StillPublishesOnlyRecordingCompleted()
    {
        var (sut, room, published) = Build();

        var outcome = await sut.ApplyAsync(EgressInfo(
            """
            {"egressId":"EG_x","roomName":"room-1","status":"EGRESS_COMPLETE",
             "fileResults":[{"location":"s3://warptalk-recordings/room-1.mp4","size":1024}]}
            """));

        Assert.Equal(EgressCompletionOutcome.Published, outcome);
        Assert.Null(room.ActiveEgressId);
        var fields = Assert.Single(published);
        Assert.Equal(MeetingEventTypes.RecordingCompleted, fields["event_type"]);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingCompletedEventPayload>>(fields["envelope"])!;
        Assert.Equal("s3://warptalk-recordings/room-1.mp4", envelope.Payload.FileUrl);
        Assert.Equal(1024, envelope.Payload.FileSizeBytes);
    }

    private static JsonElement EgressInfo(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static (EgressCompletion Sut, MeetingRoom Room, List<Dictionary<string, string>> Published) Build(
        bool publishSucceeds = true)
    {
        var room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = Guid.NewGuid(),
            ProviderRoomName = "room-1",
            ActiveEgressId = "EG_x",
        };

        // Evaluates the predicate — a stub that returns the room for any query cannot tell a lookup
        // that matches from one that does not (see MeetingWebhookRecordingTests.CreateUnitOfWork).
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

        var published = new List<Dictionary<string, string>>();
        var redis = new Mock<IRedisService>();
        redis
            .Setup(r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, fields) => published.Add(fields))
            .ReturnsAsync(publishSucceeds ? Result.Success() : Result.Failure("redis unavailable", "REDIS_ERROR"));

        return (new EgressCompletion(unitOfWork.Object, redis.Object, NullLogger<EgressCompletion>.Instance), room, published);
    }
}

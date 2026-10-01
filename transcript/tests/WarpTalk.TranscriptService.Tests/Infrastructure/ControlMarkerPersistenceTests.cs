using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Domain;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// The __MEETING_END__ sentinel rides stt:results and was persisted as a transcript segment from
/// "System" once per End call — prod, 30 Sep: six of them in one transcript within 0.7 s. It is
/// pipeline signalling, not speech: acknowledged, never stored.
/// </summary>
public class ControlMarkerPersistenceTests
{
    [Theory]
    [InlineData("__MEETING_END__", "system", true)]
    [InlineData("__MEETING_END__", "vi", true)]
    [InlineData("__MEETING_END__a", "en", true)]
    [InlineData("anything", "system", true)]
    [InlineData("Xin chào mọi người", "vi", false)]
    [InlineData("__init__ is a Python method", "en", false)]
    [InlineData("the __MEETING_END__ marker", "en", false)]
    public void IsControlMarker_RecognisesMarkersByShape(string text, string language, bool expected)
    {
        Assert.Equal(expected, ControlMarkers.IsControlMarker(text, language));
    }

    [Fact]
    public async Task TheMeetingEndSentinel_IsAcknowledgedWithoutTouchingTheTranscript()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork);
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var service = new TranscriptRedisConsumerService(
            redis,
            NullLogger<TranscriptRedisConsumerService>.Instance,
            services.BuildServiceProvider());

        // Exactly the fields MeetingRoomService.EndMeetingAsync publishes.
        var sentinel = new StreamEntry("1790756690194-0",
        [
            new NameValueEntry("segment_id", Guid.NewGuid().ToString()),
            new NameValueEntry("meeting_id", Guid.NewGuid().ToString()),
            new NameValueEntry("speaker_id", "system"),
            new NameValueEntry("text", "__MEETING_END__"),
            new NameValueEntry("language", "system"),
            new NameValueEntry("confidence", "1"),
            new NameValueEntry("start_ms", "0"),
            new NameValueEntry("end_ms", "0"),
            new NameValueEntry("chunk_index", "0"),
            new NameValueEntry("is_final_chunk", "1"),
            new NameValueEntry("timestamp_ms", "1790756690194"),
        ]);

        var method = typeof(TranscriptRedisConsumerService).GetMethod(
            "ProcessSttMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handled = await (Task<bool>)method.Invoke(service, ["stt:results", sentinel, CancellationToken.None])!;

        Assert.True(handled);
        Assert.Empty(unitOfWork.ReceivedCalls());
        Assert.Empty(database.ReceivedCalls());
    }
}

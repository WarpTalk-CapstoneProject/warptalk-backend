using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// WT-605. The meeting ending is the other way a pause window closes.
/// </summary>
/// <remarks>
/// Before this consumer, <c>ended_at</c> was written in exactly one place in the whole backend —
/// <c>TranscriptRecordingService.ResumeAsync</c> — and no room-end path touched the table at all.
/// A host who paused the transcript and then ended the meeting left the window open forever, and
/// an open window reads as "paused right now": the saved record printed
/// <c>Transcript paused · 10:15 PM–now</c> to a reader opening it the following week.
///
/// The stamp has to be the ROOM's end time, not the clock when this consumer ran. The gap between
/// them is queue lag plus however long this process was down, and it is printed to a reader as
/// extra minutes of a pause that did not happen.
/// </remarks>
public class TranslationRoomEndedConsumerTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    [Fact]
    public async Task ARoomThatEndedWhilePaused_ClosesTheWindowAtTheRoomsOwnEndTime()
    {
        var endedAt = new DateTime(2026, 9, 9, 22, 40, 0, DateTimeKind.Utc);
        var window = OpenWindow(startedAt: endedAt.AddMinutes(-25));
        var (consumer, windows, _) = Create(window);

        await consumer.CloseOpenPauseWindowAsync(RoomId, endedAt, CancellationToken.None);

        Assert.Equal(endedAt, window.EndedAt);
        windows.Received(1).Update(window);

        // Nobody resumed this transcript — the meeting stopped underneath it. Naming a person here
        // would put a decision in the record that no person made.
        Assert.Null(window.ResumedBy);
    }

    /// <summary>
    /// A room that ended while recording normally must come out of this untouched. The consumer
    /// reads the open window first rather than issuing a blind UPDATE precisely so a meeting that
    /// was never paused cannot acquire a pause window at the moment it ends.
    /// </summary>
    [Fact]
    public async Task ARoomThatEndedWhileRecording_TouchesNoWindow()
    {
        var (consumer, windows, _) = Create(activeWindow: null);

        await consumer.CloseOpenPauseWindowAsync(RoomId, DateTime.UtcNow, CancellationToken.None);

        windows.DidNotReceive().Update(Arg.Any<TranscriptPauseWindow>());
        await windows.DidNotReceive().AddAsync(Arg.Any<TranscriptPauseWindow>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The live flag goes with it — including when there was no window to close. The two can
    /// disagree (a Resume whose delete failed leaves the flag standing behind a properly closed
    /// window), and room end is the last honest moment to reconcile them.
    /// </summary>
    [Fact]
    public async Task TheDurablePauseFlagIsDeletedAtRoomEnd()
    {
        var (consumer, _, database) = Create(activeWindow: null);

        await consumer.CloseOpenPauseWindowAsync(RoomId, DateTime.UtcNow, CancellationToken.None);

        await database.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => (string)k! == TranscriptPauseKey.For(RoomId)),
            Arg.Any<CommandFlags>());
    }

    // ── Reading the relay envelope ───────────────────────────

    [Fact]
    public void RoomEnded_IsReadWithTheRoomsEndTimeWhenThePublisherStatesOne()
    {
        var endedAt = new DateTime(2026, 9, 9, 22, 40, 0, DateTimeKind.Utc);

        var read = TranslationRoomEndedConsumer.TryParseRoomEnded(
            JsonSerializer.Serialize(new
            {
                Command = "RoomEnded",
                RoomId = RoomId.ToString(),
                EndedAt = endedAt.ToString("O", CultureInfo.InvariantCulture),
            }),
            out var roomId,
            out var parsedEndedAt);

        Assert.True(read);
        Assert.Equal(RoomId, roomId);
        Assert.Equal(endedAt, parsedEndedAt);
        Assert.Equal(DateTimeKind.Utc, parsedEndedAt!.Value.Kind);
    }

    /// <summary>
    /// A publisher that predates WT-605's change to EndTranslationRoomAsync sends no EndedAt. The
    /// message is still ours — the window still has to close — and the caller substitutes its own
    /// clock, which is why the null is reported rather than filled in here.
    /// </summary>
    [Fact]
    public void RoomEnded_WithoutAnEndTime_IsStillOurs()
    {
        var read = TranslationRoomEndedConsumer.TryParseRoomEnded(
            JsonSerializer.Serialize(new { Command = "RoomEnded", RoomId = RoomId.ToString() }),
            out var roomId,
            out var endedAt);

        Assert.True(read);
        Assert.Equal(RoomId, roomId);
        Assert.Null(endedAt);
    }

    /// <summary>
    /// Every command on this channel arrives here — polls, kicks, breakouts, room start. Declining
    /// them has to be silent and total: this consumer is one subscriber among several and does not
    /// get to act on, or complain about, messages addressed to somebody else.
    /// </summary>
    [Theory]
    [InlineData("{\"Command\":\"RoomStarted\",\"RoomId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"Command\":\"TranscriptPaused\",\"RoomId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"Command\":\"RoomEnded\",\"RoomId\":\"\"}")]
    [InlineData("{\"Command\":\"RoomEnded\",\"RoomId\":\"not-a-guid\"}")]
    [InlineData("not json at all")]
    public void AnythingElseOnTheChannelIsIgnored(string message)
    {
        Assert.False(TranslationRoomEndedConsumer.TryParseRoomEnded(message, out _, out _));
    }

    // ── Helpers ──────────────────────────────────────────────

    private static TranscriptPauseWindow OpenWindow(DateTime startedAt) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = RoomId,
        StartedAt = startedAt,
        PausedBy = Guid.NewGuid(),
    };

    private static (TranslationRoomEndedConsumer Consumer, ITranscriptPauseWindowRepository Windows, IDatabase Database)
        Create(TranscriptPauseWindow? activeWindow)
    {
        var windows = Substitute.For<ITranscriptPauseWindowRepository>();
        windows.GetActiveWindowByRoomIdAsync(RoomId, Arg.Any<CancellationToken>()).Returns(activeWindow);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.TranscriptPauseWindows.Returns(windows);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork);
        var serviceProvider = services.BuildServiceProvider();

        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        return (
            new TranslationRoomEndedConsumer(
                redis, serviceProvider, NullLogger<TranslationRoomEndedConsumer>.Instance),
            windows,
            database);
    }
}

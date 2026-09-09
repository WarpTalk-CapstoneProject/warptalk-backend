using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

/// <summary>
/// WT-605 — closes a transcript pause window the meeting ended in the middle of.
/// </summary>
/// <remarks>
/// <c>ended_at</c> on a pause window used to be written in exactly one place in the whole backend:
/// <c>TranscriptRecordingService.ResumeAsync</c>. Nothing in any room-end path touched the table.
/// So a host who paused the transcript and then ended the meeting left the window open forever,
/// and an open window means "paused right now" to everything that reads it — the saved record
/// printed <c>Transcript paused · 10:15 PM–now</c>, and somebody opening that meeting the next
/// morning was told the transcript was being held, at that moment, in a room that had been over
/// for fourteen hours.
///
/// Here rather than in TranslationRoomService because the table is this service's. The room-end
/// side already publishes <c>RoomEnded</c> on the relay channel for the Gateway, so this listens
/// to the announcement that exists instead of inventing a second one — the same arrangement
/// <see cref="GlossaryStartedEventConsumer"/> has with <c>meeting.started</c>.
///
/// The window is stamped with the ROOM's EndedAt, carried in the message. The clock at the moment
/// this consumer runs is only the fallback (an older publisher, or a message replayed by hand),
/// because the gap between the two is queue lag plus however long this process was down, and that
/// gap is printed to a reader as extra minutes of a pause that never happened.
///
/// Pub/sub is lossy, so this cannot be the only net and is not: TranscriptRecordingService's read
/// path projects a still-open window on an ENDED room onto the room's own end time, which covers
/// both a publish this consumer never saw and every row already in the table from before this
/// existed. Belt and braces on purpose — an announcement with no backlog is a poor place to put
/// the only copy of a durable fact.
/// </remarks>
public class TranslationRoomEndedConsumer : BackgroundService
{
    // The channel TranslationRoomService already publishes RoomStarted/RoomEnded/TranslationStopped
    // on, and the Gateway already subscribes to. Reusing it, not the events on it.
    private const string CommandsChannel = "warptalk:translation-room:commands";
    private const string RoomEndedCommand = "RoomEnded";

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TranslationRoomEndedConsumer> _logger;

    public TranslationRoomEndedConsumer(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<TranslationRoomEndedConsumer> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // GUARDED, same shape and same reason as GlossaryStartedEventConsumer beside it: an
        // exception escaping ExecuteAsync trips the default BackgroundServiceExceptionBehavior
        // .StopHost and takes the whole TranscriptService process down — transcript reads, search
        // and export included — because Redis was a second late during a parallel app/infra deploy.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(RedisChannel.Literal(CommandsChannel), async (channel, message) =>
                {
                    try
                    {
                        if (message.IsNullOrEmpty)
                            return;

                        if (!TryParseRoomEnded(message.ToString(), out var roomId, out var endedAt))
                            return;

                        await CloseOpenPauseWindowAsync(roomId, endedAt ?? DateTime.UtcNow, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing RoomEnded for the transcript pause window.");
                    }
                });

                _logger.LogInformation(
                    "TranslationRoomEndedConsumer is listening to '{Channel}' for RoomEnded.", CommandsChannel);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "TranslationRoomEndedConsumer could not subscribe to '{Channel}'; retrying in {RetryDelay}. "
                    + "Transcript pause windows are NOT being closed at room end until it succeeds.",
                    CommandsChannel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>
    /// Reads a relay message as "this room ended at this instant", or declines it.
    /// </summary>
    /// <param name="endedAtUtc">
    /// The room's own end time when the publisher stated one, otherwise null — deliberately not
    /// defaulted to <c>UtcNow</c> in here, so the substitution stays visible at the call site and
    /// this stays a pure function the tests can pin down.
    /// </param>
    /// <remarks>
    /// Every command on this channel arrives here — polls, kicks, breakouts, the lot — so the
    /// cheap rejections come first and an unparseable body is a silent "not mine" rather than an
    /// error: this consumer is one subscriber among several and does not get to shout about
    /// messages addressed to somebody else.
    /// </remarks>
    internal static bool TryParseRoomEnded(string serializedCommand, out Guid roomId, out DateTime? endedAtUtc)
    {
        roomId = Guid.Empty;
        endedAtUtc = null;

        try
        {
            var payload = JsonSerializer.Deserialize<RoomEndedCommandMessage>(serializedCommand);
            if (payload is null || payload.Command != RoomEndedCommand)
                return false;

            if (!Guid.TryParse(payload.RoomId, out roomId))
                return false;

            if (!string.IsNullOrWhiteSpace(payload.EndedAt) &&
                DateTime.TryParse(
                    payload.EndedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                endedAtUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Close this room's open pause window, and drop the live flag that went with it.
    /// </summary>
    /// <remarks>
    /// A no-op when the room was not paused when it ended, which is the ordinary case — the point
    /// of reading the window first rather than issuing a blind UPDATE. A room that ended while
    /// recording normally must not acquire a pause window it never had.
    ///
    /// The Redis delete happens even when there was no window to close. The two can disagree (a
    /// Resume whose delete failed and left the flag standing behind a properly closed window), and
    /// room end is the last honest moment to reconcile them.
    /// </remarks>
    internal async Task CloseOpenPauseWindowAsync(Guid roomId, DateTime endedAtUtc, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var active = await unitOfWork.TranscriptPauseWindows.GetActiveWindowByRoomIdAsync(roomId, ct);
            if (active is not null)
            {
                active.EndedAt = endedAtUtc;
                // ResumedBy stays null on purpose. Nobody resumed this transcript; the meeting
                // stopped underneath it, and naming a person here would put a decision in the
                // record that no person made.
                active.UpdatedAt = DateTime.UtcNow;
                unitOfWork.TranscriptPauseWindows.Update(active);
                await unitOfWork.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Closed the open transcript pause window for room {RoomId} at the room's end time {EndedAt}.",
                    roomId, endedAtUtc);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged and swallowed: the read path repairs the display for a window this missed, so
            // a database blip here costs accuracy in the table, not correctness on the screen.
            _logger.LogWarning(ex, "Could not close the transcript pause window for room {RoomId}", roomId);
        }

        await ClearPauseFlagAsync(roomId, ct);
    }

    private async Task ClearPauseFlagAsync(Guid roomId, CancellationToken ct)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(TranscriptPauseKey.For(roomId));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The key carries a TTL precisely so this failure is bounded, and the room is over
            // either way — nothing is producing segments for the gate to hold back any more.
            _logger.LogWarning(ex, "Could not clear the transcript-paused flag for room {RoomId}", roomId);
        }
    }

    /// <summary>
    /// The fields this consumer reads off the relay envelope. A deliberately narrow view of
    /// TranslationRoomCommandMessage — the Gateway owns that type and it carries a dozen members
    /// for events this service has no interest in.
    /// </summary>
    internal sealed class RoomEndedCommandMessage
    {
        public string Command { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;

        /// <summary>
        /// Round-trip UTC, written by TranslationRoomService.EndTranslationRoomAsync. Absent on a
        /// message from a publisher that predates WT-605's change there.
        /// </summary>
        public string? EndedAt { get; set; }
    }
}

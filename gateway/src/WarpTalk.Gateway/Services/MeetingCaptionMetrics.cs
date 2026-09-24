using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using WarpTalk.Shared;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// The half of the live-meeting success rate only the Gateway can see: a caption actually leaving
/// for a room.
///
/// The AI workers can say a sentence was transcribed; only this consumer knows it was delivered to
/// the people in the meeting. So the Gateway owns two facts:
///   warptalk_meeting_captions_delivered_total{kind}      every transcript / translation / dub sent
///   warptalk_meeting_captions_started_total              rooms that received their FIRST caption
///   warptalk_meeting_time_to_first_caption_seconds       first join → first caption, per meeting
/// and writes <see cref="MeetingLifecycleKeys.FirstCaptionAt(string)"/>, which TranslationRoomService
/// reads when the room ends to decide whether the meeting reached live.
///
/// Best effort, always. This sits on the caption hot path, and a metric that could delay or fail a
/// caption would be measuring the damage it caused.
/// </summary>
public sealed class MeetingCaptionMetrics
{
    public const string MeterName = "warptalk-gateway";

    public const string KindTranscript = "transcript";
    public const string KindTranslation = "translation";
    public const string KindAudio = "audio";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Delivered = Meter.CreateCounter<long>(
        "meeting.captions.delivered",
        description: "Live results delivered to a room, by kind.");

    private static readonly Counter<long> CaptionsStarted = Meter.CreateCounter<long>(
        "meeting.captions_started",
        description: "Rooms that received their first live caption.");

    private static readonly Histogram<double> TimeToFirstCaption = Meter.CreateHistogram<double>(
        "meeting.time_to_first_caption",
        unit: "s",
        description: "First join to first delivered caption, per meeting.");

    private readonly RedisStreamService _redis;
    private readonly ILogger _logger;

    /// <summary>
    /// Rooms this replica has already marked, so the Redis write happens once per room per
    /// replica rather than once per sentence. Same per-room shape, and the same practical bound,
    /// as the consumer's other per-room caches.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _marked = new(StringComparer.OrdinalIgnoreCase);

    public MeetingCaptionMetrics(RedisStreamService redis, ILogger logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public static void RecordDelivered(string kind) =>
        Delivered.Add(1, new KeyValuePair<string, object?>("kind", kind));

    /// <summary>
    /// Marks the room's first caption. Only the writer that creates the shared marker counts the
    /// meeting, so replicas racing on the same room count it once.
    /// </summary>
    public async Task MarkFirstCaptionAsync(string translationRoomId)
    {
        if (string.IsNullOrWhiteSpace(translationRoomId) || !_marked.TryAdd(translationRoomId, 0))
        {
            return;
        }

        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var won = await _redis.SetIfAbsentAsync(
                MeetingLifecycleKeys.FirstCaptionAt(translationRoomId),
                nowMs.ToString(CultureInfo.InvariantCulture),
                MeetingLifecycleKeys.Ttl);
            if (!won)
            {
                return;
            }

            CaptionsStarted.Add(1);

            var startedRaw = await _redis.GetStringAsync(MeetingLifecycleKeys.StartedAt(translationRoomId));
            if (long.TryParse(startedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startedMs)
                && nowMs >= startedMs)
            {
                TimeToFirstCaption.Record((nowMs - startedMs) / 1000.0);
            }
        }
        catch (Exception ex)
        {
            // Forget the room so the next caption tries again rather than never.
            _marked.TryRemove(translationRoomId, out _);
            _logger.LogDebug(ex, "Could not mark the first caption for room {RoomId}", translationRoomId);
        }
    }
}

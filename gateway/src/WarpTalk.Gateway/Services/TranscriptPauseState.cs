using System.Collections.Concurrent;
using StackExchange.Redis;
using WarpTalk.Shared;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// WT-605 — whether a room's transcript is paused right now, as the Gateway's hot paths need it.
/// </summary>
/// <remarks>
/// Pause Transcript stops the transcript being WRITTEN DOWN. Translation, dubbing, subtitles and
/// LiveKit keep running: people still hear each other in their own language throughout, and the
/// only thing that stops is the record. So this answers exactly one question for exactly one lane
/// — <c>stt:results</c> → <c>TranscriptSegmentReceived</c> — and nothing else in this process may
/// consult it to decide whether to deliver translation or audio.
///
/// It reads <see cref="TranscriptPauseKey"/>, written by TranscriptService. The Gateway cannot ask
/// the database instead: its consumer group is deliberately isolated from TranscriptService's
/// persistence group and holds no connection to that schema, which is why the flag was projected
/// into Redis in the first place.
///
/// CACHED FOR SECONDS, NOT HOURS, and that bound is the whole design. Neighbouring caches in this
/// file's sibling (<c>_roomPolicyCache</c>, which never expires; <c>AiPolicyTtl</c>, four hours)
/// hold answers that cannot change while a meeting runs. This one flips whenever the host presses
/// a button, and a long cache would turn one bug into a worse, symmetric one: Resume would appear
/// to do nothing for the rest of the meeting. The five seconds exist only to keep a busy room from
/// issuing one Redis GET per sentence; <see cref="Invalidate"/> is what actually makes a pause or
/// a resume take effect, on the pub/sub event, in the same instant the participants see the banner
/// change.
///
/// FAILS OPEN. A Redis this cannot reach answers "not paused", so the segment is delivered. That
/// matches TranscriptRedisConsumerService.IsRoomTranscriptPausedAsync, which persists a segment it
/// cannot ask about — and the agreement is the point. Two lanes failing in opposite directions is
/// the reliable way to make the live transcript and the saved transcript disagree about the same
/// meeting, which is a far more confusing bug than either failure alone.
/// </remarks>
public sealed class TranscriptPauseState
{
    /// <summary>
    /// Short enough that even a lost invalidation self-corrects within a breath, which is what
    /// makes the cache safe to have at all. Long enough to collapse the per-sentence read on a
    /// room where several people are talking.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranscriptPauseState> _logger;

    // translationRoomId → (answer, when it was read). Bounded in practice by rooms with live
    // speech in this process, and entries are overwritten rather than accumulated per segment.
    private readonly ConcurrentDictionary<string, (bool Paused, DateTime ReadAtUtc)> _cache = new();

    public TranscriptPauseState(IConnectionMultiplexer redis, ILogger<TranscriptPauseState> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> IsPausedAsync(string translationRoomId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(translationRoomId))
            return false;

        if (_cache.TryGetValue(translationRoomId, out var cached) &&
            DateTime.UtcNow - cached.ReadAtUtc < CacheTtl)
        {
            return cached.Paused;
        }

        bool paused;
        try
        {
            // Existence, not the value: the payload is diagnostic and TranscriptPauseKey's contract
            // is that Resume deletes the key rather than writing a "false". Parsing it here would
            // invent a second way for the two sides to disagree.
            paused = await _redis.GetDatabase().KeyExistsAsync(TranscriptPauseKey.For(translationRoomId));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // NOT cached. A transient Redis failure must not pin this room to "recording" for the
            // next five seconds of a pause the host has already pressed.
            _logger.LogWarning(
                ex,
                "Could not read the transcript-paused flag for room {RoomId}; delivering this segment",
                translationRoomId);
            return false;
        }

        _cache[translationRoomId] = (paused, DateTime.UtcNow);
        return paused;
    }

    /// <summary>
    /// Drop this room's cached answer so the next caller re-reads Redis.
    ///
    /// Called from TranslationRoomRedisSubscriberService the moment a TranscriptPaused or
    /// TranscriptResumed command arrives — the same event that moves the banner in the clients.
    /// Without it the gate would lag the button by up to <see cref="CacheTtl"/> in both
    /// directions, and the direction that matters is Resume: a participant who has been told
    /// recording is back on, watching an empty transcript, has no way to tell a cache from a
    /// broken feature.
    /// </summary>
    public void Invalidate(string translationRoomId)
    {
        if (!string.IsNullOrEmpty(translationRoomId))
            _cache.TryRemove(translationRoomId, out _);
    }
}

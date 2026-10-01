using System.Collections.Concurrent;
using System.Globalization;
using StackExchange.Redis;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// One caption observation as the capturer's desktop reports it through
/// <c>TranslationRoomHub.ReportFarSpeakerHints</c>: "Meet's captions showed <paramref name="Name"/>
/// speaking from <paramref name="TStartMs"/> to <paramref name="TEndMs"/>", both on the CLIENT's
/// <c>Date.now()</c> clock.
/// </summary>
/// <param name="Name">The speaker label Meet printed above the caption block.</param>
/// <param name="TStartMs">Client epoch ms the caption block first showed this speaker.</param>
/// <param name="TEndMs">Client epoch ms the block was last seen growing (or became stable).</param>
/// <param name="Confidence">
/// <c>"live"</c> (read while the block was on screen) or <c>"batch"</c> (recovered afterwards).
/// Optional; anything else is read as live. Both are subject to the same age limit.
/// </param>
/// <param name="Stale">The desktop's own "this block is no longer live" flag. Optional.</param>
public sealed record FarSpeakerHintDto(
    string? Name,
    long TStartMs,
    long TEndMs,
    string? Confidence = null,
    bool? Stale = null);

/// <summary>One stream entry to write: a name and the SERVER-clock epoch ms it stands for.</summary>
/// <param name="Name">The normalized speaker name.</param>
/// <param name="TMs">Server-clock epoch ms; the AI shifts it back by its caption lag.</param>
public readonly record struct FarSpeakerHintEntry(string Name, long TMs);

/// <summary>What became of one <c>ReportFarSpeakerHints</c> call.</summary>
public enum FarSpeakerHintOutcome
{
    /// <summary>Authorized and within budget; zero or more entries were written.</summary>
    Accepted,

    /// <summary>Not the bridge audio owner of a live EXTERNAL_BRIDGE room. Nothing written.</summary>
    Refused,

    /// <summary>Over the room's per-second budget. Dropped silently; nothing written.</summary>
    RateLimited,
}

/// <summary>
/// Live Google Meet speaker names ("caption hints") from the bridge capturer's desktop into the
/// Redis stream the AI STT worker reads (warptalk-ai <c>shared/far_speaker.py</c>):
///
/// <code>XADD meeting:{translationRoomId}:far_speaker_hints MAXLEN ~ 2000 * name … t_ms … source meet_caption</code>
///
/// THE KEY'S ID IS THE TRANSLATION ROOM ID — the same id as every other <c>meeting:{room}:*</c> key
/// (e.g. <c>speaker_names</c> in AiResultConsumerService); the AI formats the key from
/// <c>STTResultMessage.meeting_id</c>, which is the translation room id.
///
/// HOW THE AI COUNTS, AND WHY A BLOCK BECOMES SEVERAL ENTRIES
///   <c>attribute_far_speaker</c> shifts every entry back by a lag (500 ms) and lets each entry that
///   lands inside a segment's window cast ONE vote; the name with the most votes wins. So a long
///   caption block must cast proportionally more votes than a short interjection: each hint is
///   expanded into one entry per <see cref="GridStepMs"/> of its span, on a grid aligned to absolute
///   time (multiples of 500 ms), capped at <see cref="MaxEntriesPerHint"/>, the latest kept. A span
///   too short to contain a grid point gets one entry at its midpoint.
///
///   The AI reads only the newest 64 entries (<c>CaptionHintTracker.scan_count</c>), so duplicates
///   would push real history out. Meet re-reports a growing block many times; the absolute grid
///   plus a per-(room, name) high-water mark means each point of a block is written once however
///   often the desktop repeats it.
///
/// CLOCK ALIGNMENT
///   The desktop stamps on its own clock. On receipt, <c>offset = serverNowMs - clientNowMs</c> and
///   every tStart/tEnd is moved by it. The network latency of the call itself (tens of ms) is left
///   in the offset; it is well inside the AI's lag (500 ms) and max-gap (1500 ms) tolerances. The
///   entry's <c>t_ms</c> is "when the desktop saw the caption", which is exactly what the AI's lag
///   shift expects — no lag is subtracted here.
///
/// STATE IS PER PROCESS
///   Rate budget, authorization cache and high-water marks live in this singleton. A room's capturer
///   holds one SignalR connection, so one replica sees all of its calls; on a reconnect to another
///   replica the worst case is a handful of duplicate points, which only re-votes the same name.
/// </summary>
public sealed class FarSpeakerHintIngest
{
    /// <summary>The value written to <c>source</c>; SOURCE_MEET_CAPTION on the AI side.</summary>
    public const string Source = "meet_caption";

    /// <summary>At most this many hints are read from one call; the newest are kept.</summary>
    public const int MaxHintsPerCall = 20;

    /// <summary>Name length bounds, after trimming and collapsing whitespace.</summary>
    public const int MaxNameLength = 80;

    /// <summary>A hint whose end is older than this (server clock) is dropped.</summary>
    public const long MaxHintAgeMs = 30_000;

    /// <summary>A hint starting further than this in the server's future is malformed.</summary>
    public const long MaxFutureSkewMs = 2_000;

    /// <summary>Grid spacing of the entries a caption block expands to.</summary>
    public const long GridStepMs = 500;

    /// <summary>At most this many entries per hint (10 s of span at 500 ms).</summary>
    public const int MaxEntriesPerHint = 20;

    /// <summary>At most this many entries per call; the latest are kept.</summary>
    public const int MaxEntriesPerCall = 60;

    /// <summary>Calls per room per one-second window; the excess is dropped.</summary>
    public const int MaxCallsPerSecond = 10;

    /// <summary>Approximate MAXLEN of the stream.</summary>
    public const int StreamMaxLength = 2000;

    /// <summary>Refreshed on every write so a dead room's key goes away on its own.</summary>
    public static readonly TimeSpan StreamTtl = TimeSpan.FromHours(6);

    /// <summary>How long an authorization answer (either way) is reused for one (room, user).</summary>
    public static readonly TimeSpan AuthorizationCacheTtl = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LogThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleRoomEviction = TimeSpan.FromMinutes(30);
    private const int MaxTrackedNamesPerRoom = 64;

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _time;
    private readonly ILogger<FarSpeakerHintIngest> _logger;
    private readonly ConcurrentDictionary<Guid, RoomState> _rooms = new();
    private long _lastSweepMs;

    public FarSpeakerHintIngest(
        IConnectionMultiplexer redis,
        ILogger<FarSpeakerHintIngest> logger,
        TimeProvider? time = null)
    {
        _redis = redis;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The stream key the AI reads, for a translation room id.</summary>
    public static string StreamKey(Guid translationRoomId) => $"meeting:{translationRoomId}:far_speaker_hints";

    /// <summary>
    /// Authorize, rate-limit, validate, convert and write one call's hints.
    /// </summary>
    /// <param name="translationRoomId">The room the hints belong to.</param>
    /// <param name="userId">The caller, from the JWT.</param>
    /// <param name="hints">The raw hints; null is an empty call.</param>
    /// <param name="clientNowMs">The desktop's <c>Date.now()</c> when it sent the call.</param>
    /// <param name="authorize">The capturer check (a gRPC round trip); its answer is cached briefly.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome and the number of stream entries written.</returns>
    public async Task<(FarSpeakerHintOutcome Outcome, int Written)> IngestAsync(
        Guid translationRoomId,
        string userId,
        IReadOnlyList<FarSpeakerHintDto>? hints,
        long clientNowMs,
        Func<CancellationToken, Task<bool>> authorize,
        CancellationToken ct = default)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIdleRooms(nowMs);
        var room = _rooms.GetOrAdd(translationRoomId, _ => new RoomState());
        Volatile.Write(ref room.LastTouchedMs, nowMs);

        if (!await IsAuthorizedAsync(room, userId, nowMs, authorize, ct))
        {
            if (room.ShouldLog(ref room.LastRefusalLogMs, nowMs))
            {
                _logger.LogWarning(
                    "FarSpeakerHints: refused hints for room {RoomId} from user {UserId} — not the capturer of a live "
                    + "external-bridge room (further refusals for this room are not logged for a minute).",
                    translationRoomId, userId);
            }
            return (FarSpeakerHintOutcome.Refused, 0);
        }

        if (!room.TryTakeCall(nowMs))
        {
            if (room.ShouldLog(ref room.LastRateLimitLogMs, nowMs))
            {
                _logger.LogWarning(
                    "FarSpeakerHints: room {RoomId} is over {Limit} calls/s; dropping the excess.",
                    translationRoomId, MaxCallsPerSecond);
            }
            return (FarSpeakerHintOutcome.RateLimited, 0);
        }

        if (hints is null || hints.Count == 0 || clientNowMs <= 0)
        {
            return (FarSpeakerHintOutcome.Accepted, 0);
        }

        List<FarSpeakerHintEntry> entries;
        lock (room.Gate)
        {
            entries = Expand(hints, clientNowMs, nowMs, room.HighWater);
        }

        if (entries.Count == 0)
        {
            return (FarSpeakerHintOutcome.Accepted, 0);
        }

        try
        {
            await WriteAsync(translationRoomId, entries);
        }
        catch (Exception ex)
        {
            // A label nobody needs to retry: the next caption repeats it. Never fail the hub call.
            _logger.LogWarning(ex, "FarSpeakerHints: could not write hints for room {RoomId}.", translationRoomId);
            return (FarSpeakerHintOutcome.Accepted, 0);
        }

        return (FarSpeakerHintOutcome.Accepted, entries.Count);
    }

    private async Task WriteAsync(Guid translationRoomId, List<FarSpeakerHintEntry> entries)
    {
        var db = _redis.GetDatabase();
        var key = StreamKey(translationRoomId);
        // Issued in order on one multiplexer, so stream ids follow t_ms order.
        var writes = new List<Task>(entries.Count + 1);
        foreach (var entry in entries)
        {
            writes.Add(db.StreamAddAsync(
                key,
                new NameValueEntry[]
                {
                    new("name", entry.Name),
                    new("t_ms", entry.TMs.ToString(CultureInfo.InvariantCulture)),
                    new("source", Source),
                },
                maxLength: StreamMaxLength,
                useApproximateMaxLength: true));
        }
        writes.Add(db.KeyExpireAsync(key, StreamTtl));
        await Task.WhenAll(writes);
    }

    private async Task<bool> IsAuthorizedAsync(
        RoomState room, string userId, long nowMs, Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (room.Gate)
        {
            if (room.Authorization.TryGetValue(userId, out var cached) && cached.ExpiresAtMs > nowMs)
            {
                return cached.Allowed;
            }
        }

        bool allowed;
        try
        {
            allowed = await authorize(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FarSpeakerHints: authorization check failed; refusing.");
            allowed = false;
        }

        lock (room.Gate)
        {
            if (room.Authorization.Count > 16)
            {
                room.Authorization.Clear();
            }
            room.Authorization[userId] = (allowed, nowMs + (long)AuthorizationCacheTtl.TotalMilliseconds);
        }
        return allowed;
    }

    private void SweepIdleRooms(long nowMs)
    {
        var last = Interlocked.Read(ref _lastSweepMs);
        if (nowMs - last < 60_000 || Interlocked.CompareExchange(ref _lastSweepMs, nowMs, last) != last)
        {
            return;
        }
        var cutoff = nowMs - (long)IdleRoomEviction.TotalMilliseconds;
        foreach (var (roomId, state) in _rooms)
        {
            if (Volatile.Read(ref state.LastTouchedMs) < cutoff)
            {
                _rooms.TryRemove(roomId, out _);
            }
        }
    }

    /// <summary>
    /// Pure: validate the hints, move them onto the server clock and expand each into grid entries,
    /// skipping points at or below the name's high-water mark (which is advanced).
    /// </summary>
    /// <param name="hints">The raw hints from the call.</param>
    /// <param name="clientNowMs">The client's clock when it sent the call.</param>
    /// <param name="serverNowMs">The server's clock on receipt.</param>
    /// <param name="highWater">Per casefolded name, the latest t_ms already written. Mutated.</param>
    /// <returns>The entries to write, oldest first, at most <see cref="MaxEntriesPerCall"/>.</returns>
    public static List<FarSpeakerHintEntry> Expand(
        IReadOnlyList<FarSpeakerHintDto> hints,
        long clientNowMs,
        long serverNowMs,
        IDictionary<string, long> highWater)
    {
        var offset = serverNowMs - clientNowMs;

        var spans = new List<(string Name, long Start, long End)>();
        foreach (var hint in hints)
        {
            if (hint is null)
            {
                continue;
            }
            var name = NormalizeName(hint.Name);
            if (name is null || hint.TStartMs <= 0 || hint.TEndMs < hint.TStartMs)
            {
                continue;
            }
            var start = hint.TStartMs + offset;
            var end = Math.Min(hint.TEndMs + offset, serverNowMs);
            if (end < serverNowMs - MaxHintAgeMs || start > serverNowMs + MaxFutureSkewMs)
            {
                continue;
            }
            spans.Add((name, Math.Min(start, end), end));
        }

        // The newest hints, processed oldest first so the high-water mark only moves forward.
        var kept = spans
            .OrderBy(s => s.End)
            .Skip(Math.Max(0, spans.Count - MaxHintsPerCall))
            .OrderBy(s => s.Start)
            .ToList();

        var entries = new List<FarSpeakerHintEntry>();
        foreach (var (name, start, end) in kept)
        {
            var key = name.ToLowerInvariant();
            var floor = highWater.TryGetValue(key, out var hw) ? hw : long.MinValue;

            var previous = floor;

            var points = GridPoints(start, end);
            if (points.Count == 0)
            {
                // Too short to straddle a grid point: one vote at its middle.
                points.Add(start + (end - start) / 2);
            }
            foreach (var t in points)
            {
                if (t > floor)
                {
                    entries.Add(new FarSpeakerHintEntry(name, t));
                    floor = t;
                }
            }

            if (floor > previous)
            {
                if (!highWater.ContainsKey(key) && highWater.Count >= MaxTrackedNamesPerRoom)
                {
                    highWater.Clear();
                }
                highWater[key] = floor;
            }
        }

        entries.Sort((a, b) => a.TMs.CompareTo(b.TMs));
        if (entries.Count > MaxEntriesPerCall)
        {
            entries.RemoveRange(0, entries.Count - MaxEntriesPerCall);
        }
        return entries;
    }

    /// <summary>Multiples of <see cref="GridStepMs"/> inside [start, end], the latest <see cref="MaxEntriesPerHint"/>.</summary>
    /// <param name="start">Span start, server ms.</param>
    /// <param name="end">Span end, server ms.</param>
    /// <returns>Ascending grid points.</returns>
    public static List<long> GridPoints(long start, long end)
    {
        var points = new List<long>();
        if (end < start)
        {
            return points;
        }
        var last = end - Mod(end, GridStepMs);
        var first = Math.Max(start + Mod(-start, GridStepMs), last - (MaxEntriesPerHint - 1) * GridStepMs);
        for (var t = first; t <= last; t += GridStepMs)
        {
            points.Add(t);
        }
        return points;
    }

    private static long Mod(long value, long m) => ((value % m) + m) % m;

    /// <summary>
    /// Trim and collapse whitespace; null when empty, longer than <see cref="MaxNameLength"/>, or
    /// containing a control character.
    /// </summary>
    /// <param name="raw">The name as received.</param>
    /// <returns>The normalized name, or null.</returns>
    public static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        foreach (var c in raw)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }
        var name = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length is >= 1 and <= MaxNameLength ? name : null;
    }

    private sealed class RoomState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, (bool Allowed, long ExpiresAtMs)> Authorization = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> HighWater = new(StringComparer.Ordinal);
        public long LastTouchedMs;
        public long LastRefusalLogMs = long.MinValue;
        public long LastRateLimitLogMs = long.MinValue;
        private long _windowStartMs;
        private int _callsInWindow;

        public bool TryTakeCall(long nowMs)
        {
            lock (Gate)
            {
                LastTouchedMs = nowMs;
                if (nowMs - _windowStartMs >= 1000)
                {
                    _windowStartMs = nowMs;
                    _callsInWindow = 0;
                }
                if (_callsInWindow >= MaxCallsPerSecond)
                {
                    return false;
                }
                _callsInWindow++;
                return true;
            }
        }

        public bool ShouldLog(ref long lastLogMs, long nowMs)
        {
            lock (Gate)
            {
                LastTouchedMs = nowMs;
                if (lastLogMs != long.MinValue && nowMs - lastLogMs < (long)LogThrottle.TotalMilliseconds)
                {
                    return false;
                }
                lastLogMs = nowMs;
                return true;
            }
        }
    }
}

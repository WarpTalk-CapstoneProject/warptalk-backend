using System.Globalization;
using System.Text.Json;
using WarpTalk.Shared;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public enum TranscriptResultStreamKind
{
    Unknown,
    Stt,
    Translation,
    Tts,
    CleanSentence
}

/// <summary>
/// WT-716: one tier-2 clean sentence as it arrives on <c>transcript:clean</c>, parsed and validated
/// but not yet applied. Field names on the wire are the producer's (warptalk-ai clean worker).
/// </summary>
public sealed record CleanSentenceMessage(
    Guid RoomId,
    Guid SentenceId,
    int Revision,
    Guid? SpeakerId,
    IReadOnlyList<Guid> SegmentIds,
    string CleanText,
    string Language,
    string[] Flags,
    string Source,
    DateTime? ProducedAt);

public static class TranscriptConsumerPollingPolicy
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    public static TimeSpan PendingClaimIdle { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan PendingRecoveryInterval { get; } = TimeSpan.FromSeconds(30);
    public const int RecoveryBatchSize = 10;
    public const long MaxDeliveryAttempts = 5;

    /// <remarks>
    /// <c>translate:backfill_results</c> carries post-meeting translations of lines the live
    /// pipeline never covered (see TranscriptTranslationBackfillService). It is a separate stream
    /// from <c>translate:results</c> for one reason: tts_worker reads that one, so publishing a
    /// backfill there would synthesise and bill speech for every line of a meeting that already
    /// ended. The payload is identical, which is why it classifies as <see cref="TranscriptResultStreamKind.Translation"/>
    /// and is persisted by the same code path.
    /// <para>
    /// <c>transcript:clean</c> (WT-716) carries tier-2 clean sentences. Read as the GLOBAL key like
    /// every other stream here: the producer publishes through base_worker.publish(), which writes
    /// both <c>transcript:clean:{meetingId}</c> and <c>transcript:clean</c>, and the room comes
    /// from <c>meeting_id</c> in the payload — see <see cref="TryResolveRoomId"/> for why the
    /// per-room keys are not scanned.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> InputStreams { get; } =
        ["stt:results", "translate:results", "translate:backfill_results", "tts:results", CleanSentenceStream];

    public const string CleanSentenceStream = "transcript:clean";

    public static TimeSpan DelayAfterPass(int messagesRead) =>
        messagesRead == 0 ? IdleDelay : TimeSpan.Zero;

    public static TranscriptResultStreamKind Classify(string stream) =>
        stream switch
        {
            "stt:results" => TranscriptResultStreamKind.Stt,
            "translate:results" => TranscriptResultStreamKind.Translation,
            "translate:backfill_results" => TranscriptResultStreamKind.Translation,
            "tts:results" => TranscriptResultStreamKind.Tts,
            CleanSentenceStream => TranscriptResultStreamKind.CleanSentence,
            _ => TranscriptResultStreamKind.Unknown
        };

    /// <summary>
    /// Resolves the room a result message belongs to, preferring the message payload over the
    /// stream key.
    /// </summary>
    /// <remarks>
    /// The room id used to live in the stream key (<c>stt:results:{roomId}</c>). Once this consumer
    /// moved to the three global <see cref="InputStreams"/>, those keys carry no room suffix — so
    /// the old <c>streamKey.Replace("stt:results:", "")</c> returned the key unchanged, failed to
    /// parse as a Guid, and every STT/translation/TTS message was ACKed and silently discarded.
    /// Nothing was persisted from 28/07 onwards, which is why
    /// <c>GET /api/v1/transcripts/by-room/{roomId}</c> 404s for every room (WT-199).
    ///
    /// Every <c>*ResultMessage.to_redis()</c> in warptalk-ai/shared/schemas.py carries
    /// <c>meeting_id</c>, so the payload is the authoritative source. The key suffix stays as a
    /// fallback because warptalk-ai/shared/base_worker.py's <c>publish()</c> still fans every
    /// result out to BOTH <c>{prefix}:{meetingId}</c> and <c>{prefix}</c>.
    /// </remarks>
    public static bool TryResolveRoomId(
        string stream,
        IReadOnlyDictionary<string, string> values,
        out Guid roomId)
    {
        if (values.TryGetValue("meeting_id", out var meetingId) && Guid.TryParse(meetingId, out roomId))
        {
            return true;
        }

        var separatorIndex = stream.LastIndexOf(':');
        var suffix = separatorIndex >= 0 ? stream[(separatorIndex + 1)..] : string.Empty;

        return Guid.TryParse(suffix, out roomId);
    }

    public static bool TryResolveSpeaker(
        IReadOnlyDictionary<string, string> values,
        out Guid? speakerId,
        out string speakerName)
    {
        var rawSpeakerId = values.GetValueOrDefault("speaker_id");
        if (string.Equals(rawSpeakerId, "system", StringComparison.OrdinalIgnoreCase))
        {
            speakerId = null;
            speakerName = "System";
            return true;
        }

        if (Guid.TryParse(rawSpeakerId, out var participantId))
        {
            speakerId = participantId;
            // The EXTERNAL_BRIDGE stand-in is one mixed stream of everybody on the Google Meet
            // side, not a person: it has no user row to look a name up in, and its GUID is not a
            // name anybody should read. ResolveSavedSpeakerName may replace this with a confident
            // live far-side name, and the post-meeting relabel per segment with the Meet
            // participant Google's own transcript attributes the words to.
            speakerName = IsBridgeStandIn(participantId)
                ? MeetConferenceErrorCodes.MeetSideFallbackSpeakerName
                : participantId.ToString();
            return true;
        }

        speakerId = null;
        speakerName = string.Empty;
        return false;
    }

    /// <summary>The far side of an EXTERNAL_BRIDGE room — see <see cref="ExternalBridgeConstants"/>.</summary>
    public static bool IsBridgeStandIn(Guid? speakerId) =>
        speakerId == ExternalBridgeConstants.ParticipantUserId;

    /// <summary>A live far-side speaker hint on a stand-in segment, as stt_worker publishes it.</summary>
    public sealed record FarSpeakerHint(string? Key, string? Source, float? Confidence);

    /// <summary>
    /// Reads the optional live far-side speaker fields warptalk-ai's stt_worker attaches to
    /// stand-in segments: <c>far_speaker_name</c> (stored as <c>far_speaker_key</c>),
    /// <c>far_speaker_source</c> and <c>far_speaker_confidence</c> (an invariant-culture float in
    /// 0..1). Absent or blank fields are null, and so is a confidence that is not a finite number in
    /// range — an unknown is stored as unknown, never as a made-up score.
    ///
    /// Only the stand-in's segments carry them; on any other speaker they are ignored, because a
    /// real participant's identity is their participant id, not a hint. The post-meeting relabel
    /// from Google Meet's transcript overrides whatever is stored here.
    /// </summary>
    public static FarSpeakerHint ResolveFarSpeaker(IReadOnlyDictionary<string, string> values, Guid? speakerId)
    {
        if (!IsBridgeStandIn(speakerId))
            return new FarSpeakerHint(null, null, null);

        static string? Field(IReadOnlyDictionary<string, string> v, string name)
        {
            var raw = v.GetValueOrDefault(name);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        return new FarSpeakerHint(
            Field(values, "far_speaker_name"),
            Field(values, "far_speaker_source"),
            FarSpeakerNames.ParseConfidence(values.GetValueOrDefault("far_speaker_confidence")));
    }

    /// <summary>
    /// The speaker name a segment is saved with, once its live far-side hint is known. A real
    /// participant (or "System") keeps <paramref name="resolvedName"/> untouched. A stand-in segment
    /// is named by <see cref="FarSpeakerNames.ResolveLive"/> — the live name when the producer is at
    /// least <paramref name="minConfidence"/> sure of it, "Google Meet participants" otherwise — the
    /// same rule the Gateway applies to the live line, so the two copies agree.
    /// </summary>
    public static string ResolveSavedSpeakerName(
        Guid? speakerId,
        string resolvedName,
        FarSpeakerHint hint,
        double minConfidence) =>
        IsBridgeStandIn(speakerId)
            ? FarSpeakerNames.ResolveLive(hint.Key, hint.Confidence, minConfidence)
            : resolvedName;

    /// <summary>
    /// The Redis field carrying the STT model's own confidence for a transcribed segment.
    /// </summary>
    public const string SttConfidenceField = "confidence";

    /// <summary>
    /// The Redis field carrying the STT confidence of the segment a translation was derived from.
    /// </summary>
    /// <remarks>
    /// WT-278: this used to be called "confidence" on translate:results as well, but the
    /// translator produces no score of its own — warptalk-ai/translation_worker copies the
    /// upstream <c>STTResultMessage.confidence</c> (an avg_logprob of the *audio*) onto the
    /// translation. Named for what it actually is so nothing can read it as translation quality.
    /// </remarks>
    public const string SourceSttConfidenceField = "source_stt_confidence";

    /// <summary>
    /// Reads an optional confidence off a result message payload, returning <c>null</c> when the
    /// producer did not actually report one. See <see cref="ModelConfidence"/> for the rules.
    /// </summary>
    /// <remarks>
    /// WT-277: the previous inline <c>float.TryParse(...) ? conf : 1.0f</c> turned every missing,
    /// unparsable or sentinel confidence into a stored 1.0000 — the maximum — which is why the
    /// return type here is nullable and the callers write it straight through to the nullable
    /// column instead of coalescing.
    /// </remarks>
    public static decimal? ResolveConfidence(IReadOnlyDictionary<string, string> values, string field) =>
        ModelConfidence.Parse(values.GetValueOrDefault(field));

    /// <summary>
    /// WT-716 tier 1: the cleaned text on an stt:results message, or <c>null</c> when the producer
    /// sent none.
    /// </summary>
    /// <remarks>
    /// Absent and empty are NOT the same answer and must not be folded together. Absent is "this
    /// segment was never cleaned" (an stt_worker that predates cleaning) and readers fall back to
    /// the raw text; empty is "cleaning ran and the whole segment was filler", and a clean view
    /// hides it. Coalescing either into the other is the WT-277 mistake in a new column.
    /// </remarks>
    public static string? ResolveCleanText(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue("clean_text", out var cleanText) ? cleanText : null;

    /// <summary>
    /// A comma-separated flag list (<c>clean_flags</c>, <c>flags</c>) as a trimmed, de-duplicated
    /// array. Absent or blank is an empty array. Unknown flags are kept, not filtered: the producer
    /// owns the vocabulary, and a newer flag reaching an older consumer should be stored rather
    /// than silently lost.
    /// </summary>
    public static string[] ParseFlags(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// WT-716 tier 2: parses one <c>transcript:clean</c> entry. False means the entry cannot be
    /// applied at all (no room, no sentence id, no revision, no segment ids, no text) and should
    /// take the bounded-retry-then-dead-letter path with its payload intact.
    /// </summary>
    /// <remarks>
    /// Lenient on descriptive fields, strict on the ones the upsert depends on. A missing
    /// <c>language</c> or <c>source</c> is stored as "unknown" rather than refusing the sentence —
    /// the same default the STT path uses for language — while a missing revision is refused
    /// outright: defaulting it to 0 would let any redelivery of a malformed entry overwrite nothing
    /// and a later good one be compared against a number nobody sent.
    /// </remarks>
    public static bool TryParseCleanSentence(
        string stream,
        IReadOnlyDictionary<string, string> values,
        out CleanSentenceMessage message)
    {
        message = null!;

        if (!TryResolveRoomId(stream, values, out var roomId)
            || !Guid.TryParse(values.GetValueOrDefault("sentence_id"), out var sentenceId)
            || !int.TryParse(values.GetValueOrDefault("revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            || revision < 0
            || !values.TryGetValue("clean_text", out var cleanText)
            || !TryParseSegmentIds(values.GetValueOrDefault("segment_ids"), out var segmentIds))
        {
            return false;
        }

        // "system" and absent both resolve to no participant, exactly as a segment's speaker does.
        TryResolveSpeaker(values, out var speakerId, out _);

        var producedAt = long.TryParse(values.GetValueOrDefault("timestamp_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) && ts > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
            : (DateTime?)null;

        message = new CleanSentenceMessage(
            roomId,
            sentenceId,
            revision,
            speakerId,
            segmentIds,
            cleanText,
            NonBlankOr(values.GetValueOrDefault("language"), "unknown"),
            ParseFlags(values.GetValueOrDefault("flags")),
            NonBlankOr(values.GetValueOrDefault("source"), "unknown"),
            producedAt);
        return true;
    }

    /// <summary>A JSON array of segment GUIDs, order preserved, duplicates dropped. Any element
    /// that is not a GUID makes the whole list invalid — a sentence silently missing one of its
    /// segments would render with a hole in it and look complete.</summary>
    public static bool TryParseSegmentIds(string? json, out IReadOnlyList<Guid> segmentIds)
    {
        segmentIds = Array.Empty<Guid>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var raw = JsonSerializer.Deserialize<string[]>(json);
            if (raw is null || raw.Length == 0)
            {
                return false;
            }

            var parsed = new List<Guid>(raw.Length);
            foreach (var item in raw)
            {
                if (!Guid.TryParse(item, out var id))
                {
                    return false;
                }

                if (!parsed.Contains(id))
                {
                    parsed.Add(id);
                }
            }

            segmentIds = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NonBlankOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public static bool ShouldDeadLetter(long deliveryAttempts) =>
        deliveryAttempts >= MaxDeliveryAttempts;

    public static string DeadLetterStream(string sourceStream) =>
        $"{sourceStream}:transcript-persistence:dead-letter";

    // ── Consumer-group hygiene ──────────────────────────────
    //
    // The consumer name carries a per-process Guid, so every pod that ever ran left a consumer
    // behind in transcript-persistence: 95 on stt/translate/tts:results and 22 on transcript:clean
    // (prod, 1 Oct). Same disease the gateway had (backend#487). Unlike the gateway's live
    // broadcasts, an entry pending here is DATA, so nothing in this pass acknowledges anything:
    // RecoverStaleMessagesAsync already XAUTOCLAIMs entries idle past PendingClaimIdle and
    // PROCESSES them. That claim is what empties a dead consumer's pending list, and only an
    // empty one is ever removed.

    /// <summary>
    /// A live consumer issues XREADGROUP on every stream at least every few seconds (IdleDelay is
    /// 250 ms, the error back-off 5 s), so an hour of silence means its process is gone.
    /// Deliberately far above anything a Redis blip or a slow rollout could produce.
    /// </summary>
    public static TimeSpan DeadConsumerIdle { get; } = TimeSpan.FromHours(1);

    public static TimeSpan ConsumerHousekeepingInterval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>One row of XINFO CONSUMERS — what the dead-consumer selection reads.</summary>
    public sealed record ConsumerState(string Name, long PendingCount, long IdleMs);

    /// <summary>
    /// Which consumers a housekeeping pass may delete: nothing pending, idle for at least
    /// <paramref name="minIdle"/>, and never the caller itself.
    /// </summary>
    /// <remarks>
    /// Idle is XINFO CONSUMERS <c>idle</c>, which Redis 7.2+ defines as time since the last
    /// ATTEMPTED interaction — an XREADGROUP that returned nothing counts — so a live consumer on
    /// a quiet stream never looks dead. A consumer still holding entries is never selected:
    /// DELCONSUMER would discard them, and here they are transcript lines nobody has written yet.
    /// </remarks>
    public static IReadOnlyList<string> SelectDeadConsumers(
        IEnumerable<ConsumerState> consumers,
        string selfConsumerName,
        TimeSpan minIdle) =>
        consumers
            .Where(c => c.PendingCount == 0
                && c.IdleMs >= (long)minIdle.TotalMilliseconds
                && !string.IsNullOrEmpty(c.Name)
                && !string.Equals(c.Name, selfConsumerName, StringComparison.Ordinal))
            .Select(c => c.Name)
            .ToList();
}

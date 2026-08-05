using WarpTalk.Shared;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public enum TranscriptResultStreamKind
{
    Unknown,
    Stt,
    Translation,
    Tts
}

public static class TranscriptConsumerPollingPolicy
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    public static TimeSpan PendingClaimIdle { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan PendingRecoveryInterval { get; } = TimeSpan.FromSeconds(30);
    public const int RecoveryBatchSize = 10;
    public const long MaxDeliveryAttempts = 5;

    public static IReadOnlyList<string> InputStreams { get; } =
        ["stt:results", "translate:results", "tts:results"];

    public static TimeSpan DelayAfterPass(int messagesRead) =>
        messagesRead == 0 ? IdleDelay : TimeSpan.Zero;

    public static TranscriptResultStreamKind Classify(string stream) =>
        stream switch
        {
            "stt:results" => TranscriptResultStreamKind.Stt,
            "translate:results" => TranscriptResultStreamKind.Translation,
            "tts:results" => TranscriptResultStreamKind.Tts,
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
            speakerName = participantId.ToString();
            return true;
        }

        speakerId = null;
        speakerName = string.Empty;
        return false;
    }

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

    public static bool ShouldDeadLetter(long deliveryAttempts) =>
        deliveryAttempts >= MaxDeliveryAttempts;

    public static string DeadLetterStream(string sourceStream) =>
        $"{sourceStream}:transcript-persistence:dead-letter";
}

using System.Globalization;
using WarpTalk.TranscriptService.Infrastructure.Redis;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

public class TranscriptConsumerPollingPolicyTests
{
    [Fact]
    public void DelayAfterPass_ReturnsIdleDelayWhenNoMessagesWereRead()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            TranscriptConsumerPollingPolicy.DelayAfterPass(0));
    }

    [Fact]
    public void DelayAfterPass_ReturnsZeroWhenWorkWasProcessed()
    {
        Assert.Equal(TimeSpan.Zero, TranscriptConsumerPollingPolicy.DelayAfterPass(1));
    }

    [Fact]
    public void InputStreams_UseGlobalStreamsInsteadOfScanningPerRoomKeys()
    {
        Assert.Equal(
            ["stt:results", "translate:results", "translate:backfill_results", "tts:results"],
            TranscriptConsumerPollingPolicy.InputStreams);
    }

    [Fact]
    public void InputStreams_KeepBackfilledTranslationsOffTheStreamThatDrivesSpeech()
    {
        // tts_worker reads "translate:results". Publishing a post-meeting backfill there would
        // synthesise and bill audio for every line of a meeting that already ended, so the
        // backfill has a stream of its own — persisted by the same code, heard by nobody.
        Assert.Contains("translate:backfill_results", TranscriptConsumerPollingPolicy.InputStreams);
        Assert.NotEqual("translate:results", "translate:backfill_results");
    }

    [Theory]
    [InlineData("stt:results", TranscriptResultStreamKind.Stt)]
    [InlineData("translate:results", TranscriptResultStreamKind.Translation)]
    [InlineData("translate:backfill_results", TranscriptResultStreamKind.Translation)]
    [InlineData("tts:results", TranscriptResultStreamKind.Tts)]
    [InlineData("stt:results:legacy-room", TranscriptResultStreamKind.Unknown)]
    public void Classify_RecognizesOnlyCanonicalGlobalStreams(
        string stream,
        TranscriptResultStreamKind expected)
    {
        Assert.Equal(expected, TranscriptConsumerPollingPolicy.Classify(stream));
    }

    [Theory]
    [InlineData("stt:results")]
    [InlineData("translate:results")]
    [InlineData("tts:results")]
    public void TryResolveRoomId_ReadsMeetingIdFromPayloadOnGlobalStreams(string stream)
    {
        // WT-199: the global stream key carries no room suffix, so deriving the room from the key
        // failed to parse and every message was ACKed and dropped — nothing was ever persisted.
        var expected = Guid.NewGuid();
        var values = new Dictionary<string, string> { ["meeting_id"] = expected.ToString() };

        Assert.True(TranscriptConsumerPollingPolicy.TryResolveRoomId(stream, values, out var roomId));
        Assert.Equal(expected, roomId);
    }

    [Fact]
    public void TryResolveRoomId_FallsBackToLegacyPerRoomStreamSuffix()
    {
        // warptalk-ai/shared/base_worker.py publish() still fans out to {prefix}:{meetingId} too.
        var expected = Guid.NewGuid();

        Assert.True(TranscriptConsumerPollingPolicy.TryResolveRoomId(
            $"stt:results:{expected}",
            new Dictionary<string, string>(),
            out var roomId));
        Assert.Equal(expected, roomId);
    }

    [Fact]
    public void TryResolveRoomId_PrefersPayloadOverStreamSuffix()
    {
        var fromPayload = Guid.NewGuid();
        var fromKey = Guid.NewGuid();
        var values = new Dictionary<string, string> { ["meeting_id"] = fromPayload.ToString() };

        Assert.True(TranscriptConsumerPollingPolicy.TryResolveRoomId(
            $"stt:results:{fromKey}",
            values,
            out var roomId));
        Assert.Equal(fromPayload, roomId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void TryResolveRoomId_FailsWhenNeitherSourceCarriesARoom(string meetingId)
    {
        var values = new Dictionary<string, string> { ["meeting_id"] = meetingId };

        Assert.False(TranscriptConsumerPollingPolicy.TryResolveRoomId("stt:results", values, out var roomId));
        Assert.Equal(Guid.Empty, roomId);
    }

    [Fact]
    public void TryResolveSpeaker_AcceptsSystemWithoutInventingAParticipantGuid()
    {
        var values = new Dictionary<string, string> { ["speaker_id"] = "system" };

        Assert.True(TranscriptConsumerPollingPolicy.TryResolveSpeaker(values, out var speakerId, out var speakerName));
        Assert.Null(speakerId);
        Assert.Equal("System", speakerName);
    }

    [Fact]
    public void TryResolveSpeaker_AcceptsParticipantGuid()
    {
        var expected = Guid.NewGuid();
        var values = new Dictionary<string, string> { ["speaker_id"] = expected.ToString() };

        Assert.True(TranscriptConsumerPollingPolicy.TryResolveSpeaker(values, out var speakerId, out var speakerName));
        Assert.Equal(expected, speakerId);
        Assert.Equal(expected.ToString(), speakerName);
    }

    [Fact]
    public void ResolveConfidence_StoresNullWhenTheMessageCarriesNoConfidenceAtAll()
    {
        // WT-277: this used to fall back to 1.0f, so a segment whose confidence was never reported
        // persisted as 1.0000 — the maximum — and became byte-identical to a segment the model was
        // certain about. "We do not know" must reach the nullable column as NULL.
        var values = new Dictionary<string, string>
        {
            ["segment_id"] = Guid.NewGuid().ToString(),
            ["text"] = "hello"
        };

        Assert.Null(TranscriptConsumerPollingPolicy.ResolveConfidence(
            values, TranscriptConsumerPollingPolicy.SttConfidenceField));
    }

    [Fact]
    public void ResolveConfidence_StoresNullForTheSttWorkerUnknownSentinel()
    {
        // warptalk-ai/stt_worker/model.py uses float(seg.get("avg_logprob", -1.0)): -1.0 is its
        // explicit "this event exposed no token logprobs" marker. It arrives looking like an
        // ordinary measurement, so without this it lands in the database as real data.
        var values = new Dictionary<string, string> { ["confidence"] = "-1.0" };

        Assert.Null(TranscriptConsumerPollingPolicy.ResolveConfidence(
            values, TranscriptConsumerPollingPolicy.SttConfidenceField));
    }

    [Fact]
    public void ResolveConfidence_RoundTripsAGenuineMeasurement()
    {
        // A real avg_logprob (negative, four decimals — matching stt_worker's round(x, 4) and the
        // DECIMAL(5,4) column) must survive untouched; nulling everything would be just as wrong.
        var values = new Dictionary<string, string> { ["confidence"] = "-0.3421" };

        Assert.Equal(
            -0.3421m,
            TranscriptConsumerPollingPolicy.ResolveConfidence(
                values, TranscriptConsumerPollingPolicy.SttConfidenceField));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    public void ResolveConfidence_StoresNullWhenTheValueCannotBeTrusted(string raw)
    {
        var values = new Dictionary<string, string> { ["confidence"] = raw };

        Assert.Null(TranscriptConsumerPollingPolicy.ResolveConfidence(
            values, TranscriptConsumerPollingPolicy.SttConfidenceField));
    }

    [Fact]
    public void ResolveConfidence_ParsesIndependentlyOfHostCulture()
    {
        // The producer always writes "-0.3421"; a host whose culture uses "," as the decimal
        // separator would otherwise read that as -3421 and overflow the DECIMAL(5,4) column.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("vi-VN");
            var values = new Dictionary<string, string> { ["confidence"] = "-0.3421" };

            Assert.Equal(
                -0.3421m,
                TranscriptConsumerPollingPolicy.ResolveConfidence(
                    values, TranscriptConsumerPollingPolicy.SttConfidenceField));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void ResolveConfidence_IgnoresTheLegacyConfidenceFieldOnTranslationMessages()
    {
        // WT-278: translate:results used to carry the source segment's STT avg_logprob under the
        // name "confidence", and it was persisted as if it scored the translation. The translation
        // path now reads only source_stt_confidence, so a legacy payload yields "unknown" rather
        // than silently resurrecting a number that never described the translation.
        var legacy = new Dictionary<string, string> { ["confidence"] = "-0.42" };

        Assert.Null(TranscriptConsumerPollingPolicy.ResolveConfidence(
            legacy, TranscriptConsumerPollingPolicy.SourceSttConfidenceField));

        var current = new Dictionary<string, string> { ["source_stt_confidence"] = "-0.42" };

        Assert.Equal(
            -0.42m,
            TranscriptConsumerPollingPolicy.ResolveConfidence(
                current, TranscriptConsumerPollingPolicy.SourceSttConfidenceField));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    public void ShouldDeadLetter_UsesBoundedDeliveryPolicy(long attempts, bool expected)
    {
        Assert.Equal(expected, TranscriptConsumerPollingPolicy.ShouldDeadLetter(attempts));
    }

    [Fact]
    public void DeadLetterStream_IsIsolatedPerSourceStreamAndConsumerGroup()
    {
        Assert.Equal(
            "translate:results:transcript-persistence:dead-letter",
            TranscriptConsumerPollingPolicy.DeadLetterStream("translate:results"));
    }
}

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
    public void InputStreams_UseThreeGlobalStreamsInsteadOfScanningPerRoomKeys()
    {
        Assert.Equal(
            ["stt:results", "translate:results", "tts:results"],
            TranscriptConsumerPollingPolicy.InputStreams);
    }

    [Theory]
    [InlineData("stt:results", TranscriptResultStreamKind.Stt)]
    [InlineData("translate:results", TranscriptResultStreamKind.Translation)]
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

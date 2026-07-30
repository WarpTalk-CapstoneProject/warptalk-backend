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
}

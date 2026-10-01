using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.FarSpeakers;
using WarpTalk.TranscriptService.Infrastructure.Redis;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// The saved name of a bridge stand-in segment: the live far-side name when stt_worker is sure
/// enough of it, "Google Meet participants" otherwise. The Gateway applies the same
/// <see cref="FarSpeakerNames"/> rule to the live line (AiResultConsumerService.TryResolveStandInSpeakerName).
/// </summary>
public class FarSpeakerLiveNameTests
{
    private static readonly Guid StandIn = ExternalBridgeConstants.ParticipantUserId;

    private static string Saved(Dictionary<string, string> values, Guid? speakerId, string resolvedName = "Google Meet participants", double min = 0.6)
    {
        var hint = TranscriptConsumerPollingPolicy.ResolveFarSpeaker(values, speakerId);
        return TranscriptConsumerPollingPolicy.ResolveSavedSpeakerName(speakerId, resolvedName, hint, min);
    }

    [Theory]
    [InlineData("0.6")]
    [InlineData("0.61")]
    [InlineData("1")]
    public void StandIn_NameAtOrAboveTheThreshold_IsTheName(string confidence)
    {
        var values = new Dictionary<string, string>
        {
            ["far_speaker_name"] = "  Alice Nguyen ",
            ["far_speaker_source"] = "meet_caption",
            ["far_speaker_confidence"] = confidence,
        };

        Assert.Equal("Alice Nguyen", Saved(values, StandIn));
    }

    [Theory]
    [InlineData("0.59")]
    [InlineData("0")]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("1.5")]
    public void StandIn_NameBelowTheThresholdOrWithUnknownConfidence_IsTheFallback(string? confidence)
    {
        var values = new Dictionary<string, string> { ["far_speaker_name"] = "Alice" };
        if (confidence is not null) values["far_speaker_confidence"] = confidence;

        Assert.Equal("Google Meet participants", Saved(values, StandIn));
    }

    [Fact]
    public void StandIn_WithoutAName_IsTheFallback_WhateverTheConfidence()
    {
        Assert.Equal("Google Meet participants", Saved(new Dictionary<string, string> { ["far_speaker_confidence"] = "0.99" }, StandIn));
        Assert.Equal("Google Meet participants", Saved(new Dictionary<string, string>(), StandIn));
    }

    [Fact]
    public void TheThresholdIsConfigurable()
    {
        var values = new Dictionary<string, string> { ["far_speaker_name"] = "Alice", ["far_speaker_confidence"] = "0.5" };

        Assert.Equal("Alice", Saved(values, StandIn, min: 0.5));
        Assert.Equal("Google Meet participants", Saved(values, StandIn, min: 0.9));
    }

    [Fact]
    public void ARealParticipant_IsNeverRenamed_EvenWithHintFields()
    {
        var values = new Dictionary<string, string> { ["far_speaker_name"] = "Mallory", ["far_speaker_confidence"] = "1" };

        Assert.Equal("Nhi", Saved(values, Guid.NewGuid(), resolvedName: "Nhi"));
        Assert.Equal("System", Saved(values, null, resolvedName: "System"));
    }

    [Fact]
    public void TheHintIsStillStoredAsSent_WhenTheNameIsNotShown()
    {
        var values = new Dictionary<string, string>
        {
            ["far_speaker_name"] = "Alice",
            ["far_speaker_source"] = "meet_caption",
            ["far_speaker_confidence"] = "0.3",
        };

        var hint = TranscriptConsumerPollingPolicy.ResolveFarSpeaker(values, StandIn);

        Assert.Equal("Alice", hint.Key);
        Assert.Equal("meet_caption", hint.Source);
        Assert.Equal(0.3f, hint.Confidence);
        Assert.Equal("Google Meet participants", TranscriptConsumerPollingPolicy.ResolveSavedSpeakerName(StandIn, "x", hint, 0.6));
    }

    [Fact]
    public void ALongLiveName_IsTruncatedToTheColumn()
    {
        var name = new string('a', 150);

        Assert.Equal(FarSpeakerNames.MaxLength, FarSpeakerNames.ResolveLive(name, 1f, 0.6).Length);
    }

    [Theory]
    [InlineData(null, 0.6)]
    [InlineData(0.8, 0.8)]
    [InlineData(0.0, 0.0)]
    [InlineData(6.0, 0.6)]
    [InlineData(-0.1, 0.6)]
    [InlineData(double.NaN, 0.6)]
    public void TheConfiguredThreshold_OutOfRangeIsTheDefault(double? configured, double expected)
    {
        Assert.Equal(expected, FarSpeakerNameOptions.From(configured).MinConfidence);
    }

    [Fact]
    public void TheConfigKeyAndDefault_AreTheContract()
    {
        // The Gateway reads the same key; a rename here would let the live line and the saved row
        // disagree about who spoke.
        Assert.Equal("Bridge:FarSpeakerNameMinConfidence", FarSpeakerNames.MinConfidenceConfigKey);
        Assert.Equal(0.6, FarSpeakerNames.DefaultMinConfidence);
        Assert.Equal("Google Meet participants", FarSpeakerNames.Fallback);
    }
}

using WarpTalk.Shared;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>
/// How sure stt_worker must be of a live far-side speaker name before a stand-in segment is saved
/// under it (<see cref="FarSpeakerNames"/>). Configured as
/// <see cref="FarSpeakerNames.MinConfidenceConfigKey"/>; the Gateway reads the same key for the
/// live line, so set both services together.
/// </summary>
public sealed record FarSpeakerNameOptions(double MinConfidence)
{
    public static FarSpeakerNameOptions Default { get; } = new(FarSpeakerNames.DefaultMinConfidence);

    /// <summary>From a raw configured value; absent or out of range is the default.</summary>
    public static FarSpeakerNameOptions From(double? configured) =>
        new(FarSpeakerNames.NormalizeMinConfidence(configured));
}

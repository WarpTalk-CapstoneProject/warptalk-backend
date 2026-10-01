using System;
using System.Globalization;

namespace WarpTalk.Shared;

/// <summary>
/// The display name of a bridge stand-in segment — one line spoken by somebody on the Google Meet
/// side of an EXTERNAL_BRIDGE room (<see cref="ExternalBridgeConstants.ParticipantUserId"/>).
///
/// WHY THIS IS IN SHARED
///   Two copies of the same line exist: the live one the Gateway broadcasts over SignalR straight
///   from <c>stt:results</c>, and the saved one TranscriptService writes from the same entry. They
///   must name the same person, or the transcript changes speaker the moment a reader reloads. One
///   rule, one threshold, one parse — used by both.
///
/// THE RULE
///   warptalk-ai's stt_worker may attach a live guess at who spoke (<c>far_speaker_name</c>, with
///   <c>far_speaker_source</c> and <c>far_speaker_confidence</c> 0..1). The line shows that name
///   only when the producer is at least <see cref="DefaultMinConfidence"/> sure (configurable as
///   <see cref="MinConfidenceConfigKey"/>). A name with no confidence, or a low one, is not shown:
///   "Google Meet participants" is honest, a confident wrong name is not. The post-meeting relabel
///   from Google's own transcript overrides whatever the live rule chose.
/// </summary>
public static class FarSpeakerNames
{
    /// <summary>The configuration key both services read the threshold from.</summary>
    public const string MinConfidenceConfigKey = "Bridge:FarSpeakerNameMinConfidence";

    /// <summary>The PO's default: a live name is shown at 0.6 confidence or above.</summary>
    public const double DefaultMinConfidence = 0.6;

    /// <summary>Matches transcript_segments.speaker_name's varchar(100).</summary>
    public const int MaxLength = 100;

    /// <summary>The name of a stand-in line nobody in particular is credited with.</summary>
    public const string Fallback = MeetConferenceErrorCodes.MeetSideFallbackSpeakerName;

    /// <summary>
    /// A configured threshold, or the default when it is absent or not a number in 0..1. Out of range
    /// is not clamped: a typo of 6 for 0.6 must not quietly switch live names off.
    /// </summary>
    public static double NormalizeMinConfidence(double? configured) =>
        configured is { } value && double.IsFinite(value) && value is >= 0d and <= 1d
            ? value
            : DefaultMinConfidence;

    /// <summary>
    /// The producer's <c>far_speaker_confidence</c>: an invariant-culture float in 0..1, or null when
    /// absent, unparsable, not finite or out of range — an unknown stays unknown.
    /// </summary>
    public static float? ParseConfidence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && float.IsFinite(parsed)
            && parsed is >= 0f and <= 1f
                ? parsed
                : null;
    }

    /// <summary>
    /// The name a stand-in line shows from its live hint: <paramref name="liveName"/> (trimmed,
    /// truncated to the column) when it is non-blank and <paramref name="confidence"/> is known and
    /// at least <paramref name="minConfidence"/>; <see cref="Fallback"/> otherwise.
    /// </summary>
    public static string ResolveLive(string? liveName, float? confidence, double minConfidence)
    {
        if (string.IsNullOrWhiteSpace(liveName) || confidence is not { } score || score < minConfidence)
            return Fallback;

        var name = liveName.Trim();
        return name.Length <= MaxLength ? name : name[..MaxLength];
    }
}

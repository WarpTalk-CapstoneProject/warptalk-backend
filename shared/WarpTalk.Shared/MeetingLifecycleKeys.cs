using System;

namespace WarpTalk.Shared;

/// <summary>
/// The Redis markers behind the platform's live-meeting success rate, and the one place their
/// names and lifetime are written down.
///
/// A CROSS-SERVICE CONTRACT between TranslationRoomService and the Gateway. Neither can see the
/// other's facts: the room service knows who joined and when the room ended, and only the
/// Gateway sees a caption actually leave for the room. So each writes the fact it owns here, and
/// the room service reads both when the room ends to decide whether the meeting ever reached
/// live. Renaming one side silently turns every meeting into "never reached live", which is why
/// the strings are computed here rather than spelled out at each call site.
///
/// SET NX, never overwritten. The first writer wins, so the value is the moment the thing FIRST
/// happened, and a counter guarded by the same write counts each meeting once however many
/// replicas race for it.
/// </summary>
public static class MeetingLifecycleKeys
{
    /// <summary>
    /// Seven days: past any meeting anybody would hold, so the end of a long meeting can still
    /// read its own start; short enough that an abandoned room's markers clear themselves.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>Unix milliseconds of the first successful join into the room.</summary>
    public static string StartedAt(Guid roomId) => StartedAt(roomId.ToString());

    /// <inheritdoc cref="StartedAt(Guid)"/>
    public static string StartedAt(string roomId) => $"translationRoom:{Normalize(roomId)}:metrics:started_at";

    /// <summary>Unix milliseconds of the first live caption the Gateway delivered to the room.</summary>
    public static string FirstCaptionAt(Guid roomId) => FirstCaptionAt(roomId.ToString());

    /// <inheritdoc cref="FirstCaptionAt(Guid)"/>
    public static string FirstCaptionAt(string roomId) => $"translationRoom:{Normalize(roomId)}:metrics:first_caption_at";

    /// <summary>
    /// The AI workers publish the room id as a lowercase UUID string and .NET formats a Guid the
    /// same way; normalising here keeps a producer that ever sends it upper-cased from writing a
    /// key nobody reads.
    /// </summary>
    private static string Normalize(string roomId) =>
        Guid.TryParse(roomId, out var parsed) ? parsed.ToString() : roomId.Trim().ToLowerInvariant();
}

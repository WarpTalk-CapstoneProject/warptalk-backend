namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// What flash mode is ACTUALLY doing for a room, and why.
///
/// This used to be a bare bool meaning "is there a per-room override saying on". That is not the
/// question a host is asking when they look at the switch — they are asking whether their room is
/// streaming — and the two answers diverged the moment the deployment default became on: every
/// room that had never been touched reported "off" while streaming. Flipping the switch on and
/// back off then wrote a real override and genuinely turned it off, so the display error cost the
/// host the latency it was wrong about.
///
/// <see cref="Source"/> is what keeps the two questions apart without collapsing them again.
/// </summary>
/// <param name="Enabled">Whether this room is streaming audio during speech, right now.</param>
/// <param name="Source">One of <see cref="FlashModeSources"/> — where <paramref name="Enabled"/> came from.</param>
public sealed record FlashModeStateDto(bool Enabled, string Source);

/// <summary>Where a <see cref="FlashModeStateDto.Enabled"/> value came from.</summary>
public static class FlashModeSources
{
    /// <summary>A host set this room explicitly. Their choice, and it outranks the deployment.</summary>
    public const string Room = "room";

    /// <summary>Nobody set this room; it is following what the deployment defaults to.</summary>
    public const string Deployment = "deployment";

    /// <summary>
    /// Neither is known — no override, and no ingress worker has published a default recently.
    /// <see cref="FlashModeStateDto.Enabled"/> is false here because something must be rendered,
    /// but it is a fallback rather than a reading, and a UI should say so rather than assert it.
    /// </summary>
    public const string Unknown = "unknown";
}

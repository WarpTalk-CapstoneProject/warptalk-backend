namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// What a queued summary request is FOR — the one thing the result cannot be read off its own
/// content, and therefore the one thing that has to travel on the request.
///
/// A "Standup in Japanese" summary looks byte-identical whether the host made it the meeting's
/// summary or a single reader asked to see it that way. Only the caller knows which act it was,
/// so SummaryResultConsumerWorker routes on this rather than guessing: guessing wrong in one
/// direction loses a reader's rendering, and in the other destroys what the host published.
///
/// String constants and not an enum because this crosses a Redis stream into Python, where
/// `shared/schemas.py` reads the same two words.
/// </summary>
public static class SummaryDelivery
{
    /// <summary>Replaces the room's SUMMARY_EXPORT artifact. What everyone sees changes.</summary>
    public const string Canonical = "canonical";

    /// <summary>Lands in translation_room_summary_variants. Nobody else's view changes.</summary>
    public const string Variant = "variant";

    /// <summary>
    /// Absent means canonical. Every request published before this field existed was a rewrite
    /// of the room's summary, so an in-flight message at deploy time keeps its old meaning
    /// instead of quietly becoming a cache row nobody reads.
    /// </summary>
    public static string OrDefault(string? value) =>
        string.Equals(value, Variant, System.StringComparison.OrdinalIgnoreCase) ? Variant : Canonical;
}

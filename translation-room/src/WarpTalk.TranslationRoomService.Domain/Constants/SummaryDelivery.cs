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

/// <summary>
/// HOW a summary request is to be answered — the `mode` field on `assistant:summary_requests`,
/// mirrored by warptalk-ai's SummaryRequestMessage.mode.
///
/// <see cref="Generate"/> (also the meaning of an absent field) writes a summary from the saved
/// transcript. <see cref="Translate"/> writes the SAME summary in another language from the
/// published one, which travels on the request as `source_content_json`: every section, item
/// and cited moment is kept, and only the words change. A reader switching language wants the
/// meeting's summary in their language, not a second summary that happens to be in it — and a
/// biên bản can only be read in another language if the sections line up with the document's.
/// </summary>
public static class SummaryRequestMode
{
    public const string Generate = "generate";

    public const string Translate = "translate";
}

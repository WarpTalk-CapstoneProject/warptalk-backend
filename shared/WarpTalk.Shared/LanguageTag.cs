namespace WarpTalk.Shared;

/// <summary>
/// The one place a BCP-47-ish language tag is reduced to its primary subtag.
///
/// WHY THIS IS SHARED AND NOT A PRIVATE HELPER PER FILE
///     WarpTalk stores the same language two ways on purpose. Rooms and voice profiles carry
///     locale tags ("vi-VN"); the AI side, the voice catalogue, the glossary and chat translation
///     are all keyed by the bare ISO-639-1 code ("vi"). Every boundary between those two worlds
///     therefore needs this reduction, and every boundary had been writing its own copy — which
///     is exactly how one of them came to be missing.
///
///     WT-632: the auth service keyed a rendered voice preview by the raw tag it was handed while
///     the Python worker keyed its answer by the bare code. The render happened, was cached, and
///     was then waited for at "voice:preview:{id}:vi-VN" against an answer sitting at
///     "voice:preview:{id}:vi" — twelve seconds of spinner and a paid re-synthesis on every
///     retry, forever, for anyone previewing a voice profile rather than a library voice.
///
///     Python has had `shared/lang.base_language` since the beginning. This is its counterpart,
///     so the two sides of every hand-off can be read against each other.
/// </summary>
public static class LanguageTag
{
    /// <summary>
    /// The primary subtag, lower-cased: "vi-VN" → "vi", "en_US" → "en", "vi" → "vi".
    ///
    /// A null, empty or whitespace tag reduces to an empty string rather than throwing. Callers
    /// are validating user input or reading a nullable column, and an empty result is a value
    /// they can test; an exception on a missing language would turn a blank field into a 500.
    /// </summary>
    public static string Base(string? language)
    {
        var trimmed = language?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOfAny(new[] { '-', '_' });
        return (separator < 0 ? trimmed : trimmed[..separator]).ToLowerInvariant();
    }

    /// <summary>
    /// Whether two tags name the same language, ignoring region and script.
    ///
    /// An empty or missing tag names no language and therefore matches nothing — including
    /// another empty tag. Treating two blanks as a match would silently pair rows that simply
    /// have not said what language they are.
    /// </summary>
    public static bool SameLanguage(string? left, string? right)
    {
        var leftBase = Base(left);
        return leftBase.Length > 0 && leftBase == Base(right);
    }
}

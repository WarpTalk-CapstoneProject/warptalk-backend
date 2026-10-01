using System.Text.RegularExpressions;

namespace WarpTalk.TranscriptService.Domain;

/// <summary>
/// Pipeline signalling that travels on <c>stt:results</c> dressed as speech — today the
/// <c>__MEETING_END__</c> sentinel meeting-service publishes to wake the assistant worker's
/// summary (warptalk-ai/shared/control_markers.py is the other half of this contract). It is not
/// something anybody said, so it is never stored as a transcript segment, never counted and never
/// translated.
///
/// Recognised by shape rather than by one exact string: production holds a
/// <c>__MEETING_END__a</c>, so a marker is anything that STARTS with <c>__NAME__</c>, or anything
/// in the "system" pseudo-language the sentinel is published under.
/// </summary>
public static partial class ControlMarkers
{
    public const string SystemLanguage = "system";

    public static bool IsControlMarker(string? text, string? language)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && MarkerPrefix().IsMatch(trimmed))
        {
            return true;
        }

        return string.Equals(language?.Trim(), SystemLanguage, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^__[A-Z0-9_]+__")]
    private static partial Regex MarkerPrefix();
}

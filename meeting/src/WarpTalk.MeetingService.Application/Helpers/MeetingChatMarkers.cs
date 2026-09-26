using System.Text.RegularExpressions;

namespace WarpTalk.MeetingService.Application.Helpers;

/// <summary>
/// The machine-readable notes WarpBot appends to its own answers.
///
/// A meeting WarpBot creates travels as an HTML comment at the end of the answer
/// (<c>&lt;!-- warpbot:meeting {json} --&gt;</c>, written by ai_assistant_worker/meeting_links.py).
/// Markdown renders nothing for it and the browser turns it into a meeting card. Anything that
/// treats the answer as prose — translation, a preview, a notification — must take it out first:
/// a translator rewords or drops it, and the reader then loses the card or is shown its JSON.
/// </summary>
public static partial class MeetingChatMarkers
{
    public static string WithoutMeetingMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("warpbot:meeting", StringComparison.Ordinal))
            return text ?? string.Empty;

        return BlankLines().Replace(MeetingMarker().Replace(text, string.Empty), "\n\n").Trim();
    }

    [GeneratedRegex(@"[ \t]*<!-- warpbot:meeting .*?-->[ \t]*\n?", RegexOptions.Singleline)]
    private static partial Regex MeetingMarker();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

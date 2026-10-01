using System.Text.RegularExpressions;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// A Google Meet code in its one canonical spelling — lowercase <c>xxx-xxxx-xxx</c> — from either a
/// bare code or a meet.google.com link. The same shape the web's bridge auto-room and
/// translation-room's own normalizer accept (three groups of 3-4 letters), so the code used to ask
/// Google is the code the room was created for.
/// </summary>
public static partial class GoogleMeetCodeParser
{
    [GeneratedRegex("^[a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4}$")]
    private static partial Regex BareCode();

    [GeneratedRegex(@"meet\.google\.com/([a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4})(?:[^a-z-]|$)")]
    private static partial Regex InUrl();

    public static bool TryParse(string? input, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var candidate = input.Trim().ToLowerInvariant();
        if (BareCode().IsMatch(candidate))
        {
            code = candidate;
            return true;
        }

        var match = InUrl().Match(candidate);
        if (!match.Success) return false;

        code = match.Groups[1].Value;
        return true;
    }
}

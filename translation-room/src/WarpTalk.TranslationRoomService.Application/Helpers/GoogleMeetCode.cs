using System.Text.RegularExpressions;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The one spelling of a Google Meet code the bridge stores and compares: lowercase,
/// <c>xxx-xxxx-xxx</c>. Accepts the bare code (any case, surrounding whitespace) or a
/// meet.google.com link, so the desktop can send whatever its address-bar sensor read.
///
/// The shape is the web's own (<c>bridge-auto-room.ts</c>: three groups of 3–4 letters), so a
/// code the client accepts is never refused here and vice versa.
/// </summary>
public static partial class GoogleMeetCode
{
    [GeneratedRegex("^[a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4}$")]
    private static partial Regex BareCode();

    [GeneratedRegex(@"meet\.google\.com/([a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4})(?:[^a-z-]|$)")]
    private static partial Regex InUrl();

    public static bool TryNormalize(string? input, out string code)
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

    /// <summary>The join link for a normalized code.</summary>
    public static string ToUrl(string normalizedCode) =>
        $"https://{TranslationRoomConstants.ExternalProviderGoogleMeetHost}/{normalizedCode}";
}

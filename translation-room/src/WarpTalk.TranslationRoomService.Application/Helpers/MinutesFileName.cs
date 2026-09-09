using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The name an exported biên bản arrives under on somebody's disk.
///
/// THE NUMBER LEADS, THE TITLE FOLLOWS
///     "BB-2026-0001 - Họp sprint.docx". The number first because that is the record's identity —
///     it is what a folder of minutes sorts by and what a person cites when they ask for one. The
///     title after it because a folder of BB-2026-0001, BB-2026-0002, BB-2026-0003 tells a reader
///     nothing about which meeting they are looking for, and they end up opening all three.
///
/// WHAT IS STRIPPED, AND WHAT IS NOT
///     Only what an operating system refuses: \ / : * ? " &lt; &gt; | and control characters, plus
///     the trailing dots and spaces Windows silently drops. Diacritics stay — "Họp sprint" is the
///     meeting's name, and an export that renames it "Hop sprint" has quietly corrected the user's
///     own words. HTTP carries it fine: ASP.NET writes a UTF-8 <c>filename*</c> beside the ASCII
///     fallback.
/// </summary>
public static class MinutesFileName
{
    /// <summary>
    /// How much of a meeting title survives into the name. Long enough for a real title, short
    /// enough that number + title + a deep Windows folder stays under the path limit.
    /// </summary>
    private const int MaxTitleLength = 80;

    /// <summary>The name to fall back to when a minutes somehow has no number at all.</summary>
    private const string Fallback = "bien-ban";

    public static string For(string? minutesNo, string? meetingTitle, int version, string extension)
    {
        var number = Clean(minutesNo);
        if (number.Length == 0) number = Fallback;

        // Version only appears once there is more than one, so an ordinary document does not
        // arrive looking like a revision.
        var suffix = version > 1 ? $"-v{version}" : string.Empty;

        var title = Truncate(Clean(meetingTitle), MaxTitleLength);
        var stem = title.Length == 0 ? $"{number}{suffix}" : $"{number}{suffix} - {title}";

        return $"{stem}.{extension.TrimStart('.')}";
    }

    /// <summary>Everything an operating system will not take, turned into a space.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            var drop = invalid.Contains(character) || char.IsControl(character);
            var space = drop || char.IsWhiteSpace(character);

            if (space)
            {
                // Collapsed rather than repeated: a title with a newline in it must not become a
                // name with a run of blanks through the middle.
                if (builder.Length > 0 && !lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        // Windows silently drops a trailing dot or space, which turns a name a user chose into a
        // slightly different one without telling them.
        return builder.ToString().TrimEnd(' ', '.');
    }

    private static string Truncate(string value, int limit)
    {
        if (value.Length <= limit) return value;

        var cut = value[..limit];
        var lastSpace = cut.LastIndexOf(' ');
        // Cut at a word where there is one: "Họp rà soát ngân sách quý…" beats a name that ends
        // mid-syllable.
        if (lastSpace > limit / 2) cut = cut[..lastSpace];

        return cut.TrimEnd(' ', '.') + "…";
    }
}

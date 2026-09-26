using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The two rules every downloaded document's name obeys, in one place.
///
/// WHY THIS IS SHARED RATHER THAN COPIED
///     <see cref="MinutesFileName"/> worked these out for the signed biên bản: what an operating
///     system refuses, what Windows silently drops, how far a title may run before a deep folder
///     pushes the whole path past the limit. <see cref="RecordFileName"/> needs exactly the same
///     answers for the transcript, the summary and the recording. Two copies of "which characters
///     are illegal" is how one of them ends up fixed alone, and then a meeting's minutes and its
///     transcript arrive under two different spellings of the same title.
///
/// WHAT IS STRIPPED, AND WHAT IS NOT
///     Only what an operating system refuses: \ / : * ? " &lt; &gt; | and control characters, plus
///     the trailing dots and spaces Windows silently drops. Diacritics stay — "Họp sprint" is the
///     meeting's name, and an export that renames it "Hop sprint" has quietly corrected the user's
///     own words. HTTP carries it fine: a UTF-8 <c>filename*</c> goes out beside the ASCII
///     fallback (see <see cref="ContentDispositionHeader"/>).
/// </summary>
public static class DocumentFileName
{
    /// <summary>
    /// How much of a meeting title survives into a name. Long enough for a real title, short
    /// enough that title + the rest of the name + a deep Windows folder stays under the path
    /// limit.
    /// </summary>
    public const int MaxTitleLength = 80;

    /// <summary>Everything an operating system will not take, turned into a space.</summary>
    public static string Clean(string? value)
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

    /// <summary>
    /// The first <paramref name="limit"/> characters of an already-cleaned value, cut at a word
    /// and marked with an ellipsis so the reader can see that something was left out.
    /// </summary>
    public static string Truncate(string value, int limit)
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

using System;

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
///     A transcript, a summary or a recording has no number, so its own meeting's name leads
///     instead — see <see cref="RecordFileName"/>. Different order, same characters, because both
///     go through <see cref="DocumentFileName"/>.
///
/// WHAT IS STRIPPED, AND WHAT IS NOT
///     <see cref="DocumentFileName.Clean"/> decides, and it is deliberately narrow: only what an
///     operating system refuses. Diacritics stay — "Họp sprint" is the meeting's name, and an
///     export that renames it "Hop sprint" has quietly corrected the user's own words. HTTP
///     carries it fine: ASP.NET writes a UTF-8 <c>filename*</c> beside the ASCII fallback.
/// </summary>
public static class MinutesFileName
{
    /// <summary>The name to fall back to when a minutes somehow has no number at all.</summary>
    private const string Fallback = "bien-ban";

    public static string For(string? minutesNo, string? meetingTitle, int version, string extension)
    {
        var number = DocumentFileName.Clean(minutesNo);
        if (number.Length == 0) number = Fallback;

        // Version only appears once there is more than one, so an ordinary document does not
        // arrive looking like a revision.
        var suffix = version > 1 ? $"-v{version}" : string.Empty;

        var title = DocumentFileName.Truncate(
            DocumentFileName.Clean(meetingTitle),
            DocumentFileName.MaxTitleLength);
        var stem = title.Length == 0 ? $"{number}{suffix}" : $"{number}{suffix} - {title}";

        return $"{stem}.{extension.TrimStart('.')}";
    }
}

using System;
using System.Globalization;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The name a downloaded transcript, summary or recording arrives under:
/// "Họp sprint 42 - Transcript (JA) - 2026-09-18.txt".
///
/// WHAT THIS REPLACES, AND WHY IT MATTERED
///     Every artifact used to download as "warptalk-optional_recording-3f2a…b7.mp4". That name is
///     the row's primary key with a hyphen in it. It tells the person who saved it nothing: not
///     which meeting it came from, not what it is, not when. Three recordings from three different
///     meetings land in one Downloads folder as three indistinguishable hex strings, and the only
///     way back to "which of these is Tuesday's standup" is to open all three. The minutes have
///     been named after their meeting since <see cref="MinutesFileName"/>; this is the same
///     courtesy for everything else the meeting produced.
///
/// THE MEETING LEADS
///     A minutes is named after its NUMBER first, because the number is the record's identity. A
///     transcript has no number, so the meeting's own name is its identity and goes first,
///     followed by what the file is.
///
/// THE DATE TRAILS
///     A weekly meeting keeps its title, so without a date seven standups download as
///     "Daily standup - Transcript.txt", "…(1).txt", "…(2).txt" — numbered by the browser, in the
///     order they happened to be clicked, which is not the order they happened. ISO order so a
///     folder sorts by it.
///
/// ONE MEETING, SEVERAL RECORDINGS
///     A host can stop and start recording inside one meeting, and each pass is its own row. They
///     share a title and a date, so the ordinal is what separates them — and it counts in the
///     order the recordings STARTED, not the order the rows were written, because egress finishes
///     out of order often enough that CreatedAt would shuffle them.
///
/// KEEP IN STEP WITH THE WEB HELPER
///     `recordFileName` in warptalk-web (src/lib/documents/record-file-name.ts) builds the same
///     name for the documents the browser writes itself. Same formula, same 80-character limit,
///     same refused characters. One deliberate difference: an over-long title is cut at a word and
///     marked "…" here (<see cref="DocumentFileName.Truncate"/>, which the minutes have always
///     done) where the web helper cuts mid-word. The server's spelling is the one to converge on.
/// </summary>
public static class RecordFileName
{
    /// <summary>What the file is, in the name. The set is closed on purpose: these three are the
    /// artifacts a person downloads to keep, and a fourth kind needs a decision, not a string.</summary>
    public const string Transcript = "Transcript";

    /// <inheritdoc cref="Transcript"/>
    public const string Summary = "Summary";

    /// <inheritdoc cref="Transcript"/>
    public const string Recording = "Recording";

    /// <param name="meetingTitle">The room's title. Null, blank or all-refused becomes "Meeting":
    /// a name the file system will take is worth more than an honest empty one.</param>
    /// <param name="kind">One of <see cref="Transcript"/>, <see cref="Summary"/>,
    /// <see cref="Recording"/>.</param>
    /// <param name="startedAt">When the meeting began, UTC. Omitted from the name when unknown
    /// rather than guessed — a wrong date is worse than no date, because it is believed.</param>
    /// <param name="language">The language this particular reading is in, for a translated
    /// summary. Omitted for a transcript as spoken and for a recording, which have only one.</param>
    /// <param name="extension">With or without its leading dot.</param>
    /// <param name="ordinal">1 for the only one of its kind in the meeting, 2 upwards for the
    /// second and later recordings, which would otherwise be identical names.</param>
    public static string For(
        string? meetingTitle,
        string kind,
        DateTime? startedAt,
        string? language,
        string extension,
        int ordinal = 1)
    {
        var title = DocumentFileName.Truncate(
            DocumentFileName.Clean(meetingTitle),
            DocumentFileName.MaxTitleLength);
        if (title.Length == 0) title = "Meeting";

        var what = DocumentFileName.Clean(kind);
        if (what.Length == 0) what = "File";
        if (ordinal > 1) what += $" ({ordinal})";

        var tag = DocumentFileName.Clean(language).ToUpperInvariant();
        if (tag.Length > 0) what += $" ({tag})";

        // default(DateTime) is not a date anybody chose — it is an unset column read as
        // 0001-01-01, and printing it would put a plausible-looking lie in the file name.
        var date = startedAt.HasValue && startedAt.Value != default
            ? startedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

        var stem = date == null ? $"{title} - {what}" : $"{title} - {what} - {date}";
        return $"{stem}.{extension.TrimStart('.')}";
    }
}

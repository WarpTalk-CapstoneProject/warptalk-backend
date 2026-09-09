using System;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// The document layouts a biên bản can be exported in.
///
/// TWO LAYOUTS OVER ONE CONTENT
///     Both render the same <c>MeetingMinutesContent</c>. Nothing about the record changes with
///     the choice — only how it is set on the page. A workspace sending the same meeting to a
///     domestic partner and to a foreign one needs both, and needs them to say the same thing.
///
/// WHY <see cref="GlobalEn"/> IS THE DEFAULT
///     A reader who does not read Vietnamese cannot check a document set in Nghị định 30 form,
///     and a wrong default in that direction is unreadable rather than merely unfamiliar. The
///     Vietnamese layout stays a first-class choice, never a fallback: for a domestic filing it is
///     the correct one, and a partner who expects it will reject anything else.
/// </summary>
public static class MinutesTemplates
{
    /// <summary>
    /// A4, 1 inch margins, Calibri 11, decimal section numbering, ISO 8601 dates, times with a
    /// UTC offset, and a document control table. Content follows Robert's Rules: what was
    /// decided, who moved it, who seconded it, and how it carried.
    /// </summary>
    public const string GlobalEn = "global-en";

    /// <summary>
    /// Nghị định 30/2020/NĐ-CP form: Times New Roman 13, 30mm left margin, Roman numeral
    /// headings, and the section order a Vietnamese reader expects to find them in.
    /// </summary>
    public const string VnNd30 = "vn-nd30";

    public const string Default = GlobalEn;

    public static bool IsKnown(string? template) =>
        string.Equals(template, GlobalEn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(template, VnNd30, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The template to actually render, given whatever the caller asked for.
    ///
    /// An unknown value falls back to the default rather than failing the download. The choice is
    /// presentation: refusing to hand somebody their own minutes because a query string carried a
    /// typo would trade a readable document for no document.
    /// </summary>
    public static string Normalise(string? template) => template switch
    {
        null => Default,
        _ when string.Equals(template, GlobalEn, StringComparison.OrdinalIgnoreCase) => GlobalEn,
        _ when string.Equals(template, VnNd30, StringComparison.OrdinalIgnoreCase) => VnNd30,
        _ => Default
    };
}

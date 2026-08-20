using System;
using System.Globalization;
using System.Text;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// Folds user-typed text into a comparable form for substring search.
///
/// Vietnamese names carry diacritics that nobody types when searching — "manh" has to find
/// "Mạnh" (WT-231). Folding both the needle and the haystack the same way makes the match
/// diacritic- and case-insensitive without needing a collation change in Postgres.
///
/// Separators fold too, for the same reason. A document named BUG-TRACKING-WT478-494 is read
/// aloud, and typed, as "bug tracking" — nobody reproduces the hyphens a file name happened to
/// use. Before this, WarpBot answered "no document whose name contains bug tracking" while the
/// file sat one row away in the list, because "bug tracking" is not a substring of
/// "bug-tracking-wt478-494". Hyphen, underscore, dot and slash all become one space, so the
/// haystack is compared as the words it is made of rather than as the punctuation someone chose.
/// </summary>
public static class SearchTextHelper
{
    /// <summary>
    /// Lowercases and strips diacritics. Returns an empty string for null/whitespace input.
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // đ/Đ is a distinct letter, not a base letter plus a combining mark, so FormD leaves
        // it intact — it has to be mapped explicitly or "Đặng" never folds to "dang".
        var normalized = value.Trim()
            .Replace('Đ', 'D')
            .Replace('đ', 'd')
            .Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(normalized.Length);
        var pendingSeparator = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // One space stands for any run of separators, so "bug tracking", "bug-tracking" and
            // "bug_tracking" all fold to the same thing. Collapsing a run rather than replacing
            // each character keeps "WT478 - 494" from folding to "wt478   494", which no typed
            // term would ever match.
            if (IsSeparator(c))
            {
                pendingSeparator = sb.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                sb.Append(' ');
                pendingSeparator = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="term"/> appears anywhere in <paramref name="value"/> once both
    /// are folded. An empty term matches everything — callers guard before narrowing a list.
    /// </summary>
    public static bool Matches(string? value, string? term)
    {
        var foldedTerm = Fold(term);
        if (foldedTerm.Length == 0)
            return true;

        return Fold(value).Contains(foldedTerm, StringComparison.Ordinal);
    }

    /// <summary>
    /// Characters that separate words rather than carry meaning for a search. Whitespace, plus
    /// the punctuation file names and slugs are built out of.
    /// </summary>
    private static bool IsSeparator(char c) =>
        char.IsWhiteSpace(c) || c is '-' or '_' or '.' or '/' or '\\';
}

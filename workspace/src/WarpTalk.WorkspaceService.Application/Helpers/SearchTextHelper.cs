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
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
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
}

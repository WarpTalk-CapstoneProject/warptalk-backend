using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WarpTalk.WorkspaceService.Application.Masking;

/// <summary>A place in a string where a hidden value sits.</summary>
public readonly record struct PiiMatch(int Start, int Length, string Placeholder);

/// <summary>
/// Finds the scan's hidden values in a piece of text, and replaces them.
/// </summary>
/// <remarks>
/// ONE definition of "this value is here", shared by the code that rewrites a file and the code
/// that verifies the rewrite. If the two disagreed, verification would pass a file the rewrite
/// had not finished — so they do not get to disagree.
///
/// A match must stand on its own: a value that starts or ends with a letter or digit is not
/// matched in the middle of a longer word or number. Without that, hiding the name "An" would
/// rewrite every "And" in the document, and — worse for the check — every "And" left behind would
/// read as a leak.
///
/// Each value is searched in both Unicode normal forms. The scan works on NFC text; a file typed
/// with decomposed Vietnamese diacritics holds the same name as different code points.
/// </remarks>
public sealed class PiiValueMatcher
{
    private readonly List<PiiReplacement> _needles;

    public PiiValueMatcher(IEnumerable<PiiReplacement> replacements)
    {
        var needles = new Dictionary<string, PiiReplacement>(StringComparer.Ordinal);
        foreach (var replacement in replacements)
        {
            var value = replacement.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var form in new[]
                     {
                         value,
                         value.Normalize(NormalizationForm.FormC),
                         value.Normalize(NormalizationForm.FormD)
                     })
            {
                needles.TryAdd(form, new PiiReplacement(form, replacement.Placeholder));
            }
        }

        // Longest first, so "0901 234 567" is hidden whole rather than as the "0901" inside it.
        _needles = needles.Values.OrderByDescending(n => n.Value.Length).ToList();
    }

    public bool IsEmpty => _needles.Count == 0;

    public int Count => _needles.Count;

    /// <summary>Non-overlapping matches, in order of position.</summary>
    public List<PiiMatch> FindMatches(string? text)
    {
        var matches = new List<PiiMatch>();
        if (string.IsNullOrEmpty(text) || _needles.Count == 0)
        {
            return matches;
        }

        foreach (var needle in _needles)
        {
            var from = 0;
            while (from <= text.Length - needle.Value.Length)
            {
                var at = text.IndexOf(needle.Value, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                var end = at + needle.Value.Length;
                if (StandsAlone(text, at, end) && !Overlaps(matches, at, end))
                {
                    matches.Add(new PiiMatch(at, needle.Value.Length, needle.Placeholder));
                    from = end;
                }
                else
                {
                    from = at + 1;
                }
            }
        }

        matches.Sort((a, b) => a.Start.CompareTo(b.Start));
        return matches;
    }

    public bool ContainsAny(string? text) => FindMatches(text).Count > 0;

    /// <summary>The text with every match replaced by its marker.</summary>
    public string Replace(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var matches = FindMatches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text, position, match.Start - position);
            builder.Append(match.Placeholder);
            position = match.Start + match.Length;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    private static bool StandsAlone(string text, int start, int end)
    {
        if (char.IsLetterOrDigit(text[start]) && start > 0 && char.IsLetterOrDigit(text[start - 1]))
        {
            return false;
        }

        if (char.IsLetterOrDigit(text[end - 1]) && end < text.Length && char.IsLetterOrDigit(text[end]))
        {
            return false;
        }

        return true;
    }

    private static bool Overlaps(List<PiiMatch> matches, int start, int end)
    {
        foreach (var match in matches)
        {
            if (start < match.Start + match.Length && match.Start < end)
            {
                return true;
            }
        }

        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WarpTalk.WorkspaceService.Application.Masking;

/// <summary>
/// One thing the security scan hid: the exact text it found, and the marker it put there.
/// </summary>
/// <remarks>
/// <see cref="Value"/> IS the personal data. It lives in memory for the length of one masking
/// pass and must never be logged, audited or written anywhere but the encrypted document store.
/// ToString is overridden so that an accidental <c>{Replacement}</c> in a log template prints the
/// marker and a length, not the value.
/// </remarks>
public sealed record PiiReplacement(string Value, string Placeholder)
{
    public override string ToString() => $"{Placeholder} ({Value.Length} chars)";
}

/// <summary>
/// Recovers WHICH text the security scan hid, from the two things the scan gives back: the text
/// that went in and the masked text that came out.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The security worker returns the whole document with its PII replaced by
/// markers and nothing else — no spans, no values. That is enough to index, and not enough to
/// produce a masked copy of the FILE: a .docx is rewritten run by run, so the masker needs to know
/// what to look for.
///
/// HOW. The masked text is the original with holes in it, so the text either side of each marker
/// is an anchor that also occurs in the original, and whatever sits between two anchors in the
/// original is what the marker replaced. The comparison ignores whitespace, because the model
/// that echoes the text back does not reliably preserve it, and anchors on a short context either
/// side of each marker rather than on the whole segment, so a stray edit far from a marker does
/// not sink the alignment.
///
/// WHEN IT CANNOT. A context that is not found, or a hole wider than any real personal detail,
/// means the two texts do not line up. The answer then is null — "no masked copy can be made" —
/// and never a best guess: a wrong value here is a file that still shows what it claims to hide.
/// </remarks>
public static class PiiMaskAlignment
{
    /// <summary>Every marker the scan writes: [PII_REDACTED], [EMAIL_REDACTED], [PHONE_REDACTED]…</summary>
    public static readonly Regex PlaceholderPattern = new(
        @"\[[A-Z][A-Z_]*_REDACTED\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceholderRunPattern = new(
        @"(?:\[[A-Z][A-Z_]*_REDACTED\])+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>How much text either side of a marker is used to find its place in the original.</summary>
    private const int ContextLength = 24;

    /// <summary>
    /// The widest hole that is still believable as one personal detail (whitespace excluded). A
    /// full postal address fits; a paragraph does not.
    /// </summary>
    public const int MaxValueLength = 400;

    /// <summary>The marker written where a run of adjacent markers collapsed into one hole.</summary>
    public const string GenericPlaceholder = "[PII_REDACTED]";

    /// <summary>
    /// The values hidden between <paramref name="original"/> and <paramref name="masked"/>, or
    /// null when the two cannot be lined up. An empty list means the masked text has no markers.
    /// </summary>
    public static IReadOnlyList<PiiReplacement>? DeriveReplacements(string? original, string? masked)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(masked))
        {
            return null;
        }

        // The worker normalises to NFC before masking, so the masked text is NFC whatever the
        // file was. Aligning against the NFC form keeps the two comparable; PiiValueMatcher adds
        // the other normal form back when it searches the file.
        var source = original.Normalize(NormalizationForm.FormC);
        var target = masked.Normalize(NormalizationForm.FormC);

        var (squashedSource, sourceMap) = Squash(source);
        var (squashedTarget, _) = Squash(target);

        var runs = PlaceholderRunPattern.Matches(squashedTarget);
        if (runs.Count == 0)
        {
            return Array.Empty<PiiReplacement>();
        }

        var found = new List<PiiReplacement>();

        // `cursor` is where, in the squashed original, the text after the previous marker begins.
        var cursor = 0;
        var segmentStart = 0;

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var before = squashedTarget.Substring(segmentStart, run.Index - segmentStart);
            var afterStart = run.Index + run.Length;
            var afterEnd = i + 1 < runs.Count ? runs[i + 1].Index : squashedTarget.Length;
            var after = squashedTarget.Substring(afterStart, afterEnd - afterStart);

            var valueStart = LocateEndOfSegment(squashedSource, before, cursor);
            if (valueStart < 0)
            {
                return null;
            }

            int valueEnd;
            if (after.Length == 0)
            {
                // Nothing follows the marker, so it replaced everything up to the end.
                valueEnd = squashedSource.Length;
            }
            else
            {
                var context = after.Length > ContextLength ? after[..ContextLength] : after;
                valueEnd = squashedSource.IndexOf(context, valueStart, StringComparison.Ordinal);
                if (valueEnd < 0)
                {
                    return null;
                }
            }

            var length = valueEnd - valueStart;
            if (length > MaxValueLength)
            {
                return null;
            }

            if (length > 0)
            {
                var rawStart = sourceMap[valueStart];
                var rawEnd = sourceMap[valueEnd - 1] + 1;
                var value = source.Substring(rawStart, rawEnd - rawStart);
                var placeholder = PlaceholderPattern.Matches(run.Value).Count == 1
                    ? run.Value
                    : GenericPlaceholder;
                AddValue(found, value, placeholder);
            }

            cursor = valueEnd;
            segmentStart = afterStart;
        }

        return found
            .GroupBy(r => r.Value, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.Value.Length)
            .ToList();
    }

    /// <summary>
    /// Where the segment that precedes a marker ends in the original — which is where the hidden
    /// value begins. -1 when the segment's tail is not there.
    /// </summary>
    private static int LocateEndOfSegment(string squashedSource, string segment, int cursor)
    {
        if (segment.Length == 0)
        {
            return cursor;
        }

        // The echo is usually exact, and then the segment sits at the cursor.
        var expectedEnd = cursor + segment.Length;
        var context = segment.Length > ContextLength ? segment[^ContextLength..] : segment;
        var expectedStart = expectedEnd - context.Length;
        if (expectedStart >= 0
            && expectedEnd <= squashedSource.Length
            && string.CompareOrdinal(squashedSource, expectedStart, context, 0, context.Length) == 0)
        {
            return expectedEnd;
        }

        // It drifted: the model changed something inside the segment. Take the occurrence of the
        // tail closest to where it should have been, never one before the cursor.
        var best = -1;
        var bestDistance = int.MaxValue;
        var from = cursor;
        while (from <= squashedSource.Length - context.Length)
        {
            var at = squashedSource.IndexOf(context, from, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            var distance = Math.Abs(at - expectedStart);
            if (distance < bestDistance)
            {
                best = at;
                bestDistance = distance;
            }
            else if (at > expectedStart)
            {
                // Occurrences only get further away from here on.
                break;
            }

            from = at + 1;
        }

        if (best < 0)
        {
            return -1;
        }

        // A drift larger than the segment itself is not drift, it is a different place.
        return bestDistance <= Math.Max(segment.Length / 4, 64) ? best + context.Length : -1;
    }

    private static void AddValue(List<PiiReplacement> found, string value, string placeholder)
    {
        // A value that spans lines spans paragraphs or cells in the file, and those are rewritten
        // one at a time — so each line is hidden on its own.
        foreach (var line in value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = line.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            // The document already said "[PHONE_REDACTED]" here and the scan kept it.
            if (PlaceholderRunPattern.Match(part) is { Success: true } m && m.Length == part.Length)
            {
                continue;
            }

            found.Add(new PiiReplacement(part, placeholder));
        }
    }

    /// <summary>The text with all whitespace removed, and for each kept character its index in the input.</summary>
    private static (string Squashed, int[] Map) Squash(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ' ' || text[i] == '​' || text[i] == '﻿')
            {
                continue;
            }

            map[builder.Length] = i;
            builder.Append(text[i]);
        }

        return (builder.ToString(), map);
    }
}

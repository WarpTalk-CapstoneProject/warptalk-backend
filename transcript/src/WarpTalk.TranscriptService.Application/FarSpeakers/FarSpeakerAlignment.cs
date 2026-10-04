using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>A stand-in transcript segment on the absolute clock (Unix ms, UTC).</summary>
public sealed record StandInSegment(Guid Id, long StartMs, long EndMs, string Text);

/// <summary>One entry of Google Meet's transcript on the absolute clock (Unix ms, UTC).</summary>
public sealed record MeetTranscriptLine(string ParticipantKey, string DisplayName, long StartMs, long EndMs, string Text);

/// <summary>The Meet participant a stand-in segment is attributed to.</summary>
public sealed record FarSpeakerAssignment(Guid SegmentId, string ParticipantKey, string DisplayName, float Confidence);

/// <param name="OffsetMs">What was added to the segments' times to put them on Meet's clock.</param>
/// <param name="AnchorMatches">How many text matches the offset was estimated from; 0 = fallback scan around 0.</param>
public sealed record FarSpeakerAlignmentResult(
    long OffsetMs,
    int AnchorMatches,
    IReadOnlyList<FarSpeakerAssignment> Assignments);

/// <summary>
/// Matches the bridge stand-in's transcript segments (one mixed stream of the whole Google Meet
/// side) to the entries of Google Meet's own speaker-attributed transcript, so each segment can be
/// named after the person who said it.
///
/// Two clocks are involved: WarpTalk's segments are timed from the STT pipeline's timeline anchor,
/// Google's entries from Google's servers. They differ by capture latency, by the anchor's own
/// error and by clock skew — a few hundred milliseconds to a few seconds, and the SAME for the
/// whole meeting. So the method is: estimate one offset, then attribute by time overlap.
///
/// <list type="number">
/// <item>Offset from anchors: segments whose text closely matches a nearby entry (same words, same
/// length — see <see cref="TextSimilarity"/>) are taken as the same utterance, and the median of
/// their centre-to-centre differences is the offset. Median, so a wrong anchor cannot drag it.</item>
/// <item>No anchors (different languages, heavy STT errors, very short meeting): scan offsets within
/// ±<see cref="FallbackToleranceMs"/> of zero and keep the one that covers the most segment time,
/// preferring the one closest to zero on a tie.</item>
/// <item>Each segment, shifted by the offset, goes to the entry overlapping it most. The overlap as
/// a fraction of the segment's length is the confidence; under <see cref="MinOverlapRatio"/> the
/// segment is left alone (it keeps "Google Meet participants").</item>
/// </list>
///
/// Pure: no I/O, no clock. Everything about which segments may be touched (only the stand-in's,
/// never a host's choice) is the caller's job.
/// </summary>
public static class FarSpeakerAlignment
{
    /// <summary>Below this overlap ratio a segment is not attributed.</summary>
    public const double MinOverlapRatio = 0.3;

    /// <summary>Text similarity at or above which a segment/entry pair counts as an anchor.</summary>
    public const double AnchorSimilarity = 0.6;

    /// <summary>Anchors need some substance: "yes" matches half the meeting.</summary>
    public const int MinAnchorWords = 3;

    /// <summary>How far apart (start to start) an anchor pair may be. Bounds the offset.</summary>
    public const long MaxAnchorDistanceMs = 120_000;

    /// <summary>Half-width of the offset scan used when there are no anchors.</summary>
    public const long FallbackToleranceMs = 2_000;

    /// <summary>Step of the fallback scan.</summary>
    public const long FallbackStepMs = 250;

    public static FarSpeakerAlignmentResult Align(
        IReadOnlyList<StandInSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(entries);

        if (segments.Count == 0 || entries.Count == 0)
            return new FarSpeakerAlignmentResult(0, 0, Array.Empty<FarSpeakerAssignment>());

        var sortedEntries = entries
            .Where(e => e.EndMs >= e.StartMs)
            .OrderBy(e => e.StartMs)
            .ThenBy(e => e.EndMs)
            .ToList();
        if (sortedEntries.Count == 0)
            return new FarSpeakerAlignmentResult(0, 0, Array.Empty<FarSpeakerAssignment>());

        var offset = EstimateOffset(segments, sortedEntries, out var anchors);

        var assignments = new List<FarSpeakerAssignment>();
        foreach (var segment in segments)
        {
            var best = BestEntry(segment, sortedEntries, offset);
            if (best is null) continue;

            var (entry, ratio) = best.Value;
            if (ratio < MinOverlapRatio) continue;

            assignments.Add(new FarSpeakerAssignment(
                segment.Id,
                entry.ParticipantKey,
                entry.DisplayName,
                (float)Math.Round(Math.Min(1.0, ratio), 4)));
        }

        return new FarSpeakerAlignmentResult(offset, anchors, assignments);
    }

    /// <summary>
    /// The offset to ADD to segment times to put them on the entries' clock. See the class remarks.
    /// </summary>
    public static long EstimateOffset(
        IReadOnlyList<StandInSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries,
        out int anchorMatches)
    {
        var differences = new List<long>();
        var entryTokens = entries.Select(e => Tokens(e.Text)).ToList();

        foreach (var segment in segments)
        {
            var segmentTokens = Tokens(segment.Text);
            if (segmentTokens.Count < MinAnchorWords) continue;

            double bestSimilarity = 0;
            long bestDifference = 0;
            var bestCount = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (Math.Abs(entry.StartMs - segment.StartMs) > MaxAnchorDistanceMs) continue;

                var similarity = TokenSimilarity(segmentTokens, entryTokens[i]);
                var difference = Centre(entry.StartMs, entry.EndMs) - Centre(segment.StartMs, segment.EndMs);
                if (similarity > bestSimilarity + 1e-9)
                {
                    bestSimilarity = similarity;
                    bestDifference = difference;
                    bestCount = 1;
                }
                else if (Math.Abs(similarity - bestSimilarity) <= 1e-9 && similarity > 0)
                {
                    // The same sentence said twice within two minutes is not an anchor: either
                    // occurrence could be the one, and guessing would hand the offset a wrong value.
                    bestCount++;
                }
            }

            if (bestSimilarity >= AnchorSimilarity && bestCount == 1)
                differences.Add(bestDifference);
        }

        anchorMatches = differences.Count;
        if (differences.Count > 0)
            return Median(differences);

        return ScanAroundZero(segments, entries);
    }

    /// <summary>
    /// Word-level similarity in [0, 1]: shared words (with multiplicity) over the LONGER side's
    /// word count. Deliberately strict — a short segment fully contained in a long entry scores low,
    /// because anchors are only useful when both sides are clearly the same utterance.
    /// </summary>
    public static double TextSimilarity(string? a, string? b) =>
        TokenSimilarity(Tokens(a), Tokens(b));

    /// <summary>Lower case, Unicode-normalized, punctuation stripped, split on whitespace.</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var normalized = text.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            var keep = char.IsLetterOrDigit(ch)
                || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            builder.Append(keep ? ch : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static double TokenSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in a)
            counts[token] = counts.GetValueOrDefault(token) + 1;

        var shared = 0;
        foreach (var token in b)
        {
            if (counts.TryGetValue(token, out var left) && left > 0)
            {
                shared++;
                counts[token] = left - 1;
            }
        }

        return (double)shared / Math.Max(a.Count, b.Count);
    }

    private static long ScanAroundZero(
        IReadOnlyList<StandInSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries)
    {
        long bestOffset = 0;
        long bestCovered = Covered(segments, entries, 0);
        for (var step = FallbackStepMs; step <= FallbackToleranceMs; step += FallbackStepMs)
        {
            // Positive before negative at the same distance only to keep the result deterministic;
            // strictly-greater keeps the offset closest to zero on any tie.
            foreach (var candidate in new[] { step, -step })
            {
                var covered = Covered(segments, entries, candidate);
                if (covered > bestCovered)
                {
                    bestCovered = covered;
                    bestOffset = candidate;
                }
            }
        }

        return bestOffset;
    }

    /// <summary>Total segment time covered by its best entry at this offset.</summary>
    private static long Covered(
        IReadOnlyList<StandInSegment> segments,
        IReadOnlyList<MeetTranscriptLine> entries,
        long offset)
    {
        long total = 0;
        foreach (var segment in segments)
        {
            long best = 0;
            foreach (var entry in entries)
                best = Math.Max(best, Overlap(segment.StartMs + offset, segment.EndMs + offset, entry.StartMs, entry.EndMs));
            total += best;
        }

        return total;
    }

    private static (MeetTranscriptLine Entry, double Ratio)? BestEntry(
        StandInSegment segment,
        IReadOnlyList<MeetTranscriptLine> entries,
        long offset)
    {
        var start = segment.StartMs + offset;
        var end = segment.EndMs + offset;
        var duration = end - start;

        MeetTranscriptLine? best = null;
        double bestRatio = 0;
        double bestSimilarity = -1;
        foreach (var entry in entries)
        {
            double ratio;
            if (duration <= 0)
            {
                // A zero-length segment is a point: inside an entry or not.
                ratio = start >= entry.StartMs && start <= entry.EndMs ? 1.0 : 0.0;
            }
            else
            {
                ratio = (double)Overlap(start, end, entry.StartMs, entry.EndMs) / duration;
            }

            if (ratio <= 0) continue;

            // Ties (a segment covered equally by two entries — e.g. two people talking over each
            // other) go to the entry whose words match better; failing that, the earlier entry,
            // which the ordering of `entries` already gives.
            if (ratio > bestRatio + 1e-9)
            {
                best = entry;
                bestRatio = ratio;
                bestSimilarity = -1;
            }
            else if (Math.Abs(ratio - bestRatio) <= 1e-9 && best is not null)
            {
                if (bestSimilarity < 0) bestSimilarity = TextSimilarity(segment.Text, best.Text);
                var similarity = TextSimilarity(segment.Text, entry.Text);
                if (similarity > bestSimilarity + 1e-9)
                {
                    best = entry;
                    bestSimilarity = similarity;
                }
            }
        }

        return best is null ? null : (best, bestRatio);
    }

    private static long Overlap(long aStart, long aEnd, long bStart, long bEnd) =>
        Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

    private static long Centre(long start, long end) => start + (end - start) / 2;

    private static long Median(List<long> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2;
    }
}

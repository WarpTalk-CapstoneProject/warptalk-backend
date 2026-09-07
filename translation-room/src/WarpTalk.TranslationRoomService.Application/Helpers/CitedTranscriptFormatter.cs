using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// Renders stored transcript segments in the shape a summariser cites against:
/// <c>[t=&lt;ms&gt;] [speaker] text</c>.
///
/// MIRRORS <c>format_transcript_line</c> in warptalk-ai's <c>ai_assistant_worker/summary_templates.py</c>.
/// Two copies of a format is a drift risk, taken deliberately: the alternative is for
/// <c>ArtifactsFinalizer</c> to make an authenticated HTTP call into the transcript service so the
/// AI worker can re-read segments this process has already read over gRPC — and the only ways to
/// authenticate a background job there are a service credential or an unauthenticated call, both
/// of which are the "privileged bypass" <c>RegenerateSummaryAsync</c> exists to refuse.
///
/// THE OFFSET IS RELATIVE TO THE FIRST SEGMENT, and that is the whole reason this is a named
/// helper rather than an inline string interpolation. The stored transcript is rendered on the
/// meeting page as a base time plus a per-segment offset, so a cited <c>atMs</c> is resolved the
/// same way. Emitting absolute epoch milliseconds would produce citations that all parse, all look
/// plausible, and every one of which points at a moment the meeting does not contain.
/// </summary>
public static class CitedTranscriptFormatter
{
    /// <summary>One segment as the summariser needs to see it.</summary>
    public readonly record struct Segment(int StartMs, string Speaker, string Text);

    /// <summary>
    /// The cited transcript, or an empty string when nothing was actually said.
    ///
    /// Blank segments are dropped rather than rendered. A line with no words carries no evidence
    /// and costs prompt budget, and WT-478 established that a wall of empty scaffolding is
    /// non-empty to code while being empty to a reader — which is how a contentless transcript
    /// once reached the model and came back reported as a real summary.
    /// </summary>
    public static string Format(IEnumerable<Segment> segments)
    {
        var spoken = segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (spoken.Count == 0) return string.Empty;

        var baseMs = spoken.Min(s => s.StartMs);

        return string.Join(
            "\n",
            spoken.Select(s => $"[t={Math.Max(s.StartMs - baseMs, 0)}] [{s.Speaker}] {s.Text}"));
    }
}

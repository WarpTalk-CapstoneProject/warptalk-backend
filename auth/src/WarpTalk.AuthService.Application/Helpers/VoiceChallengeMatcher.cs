using System;
using System.Globalization;
using System.Text;

namespace WarpTalk.AuthService.Application.Helpers;

/// <summary>
/// WT-888 — how closely a transcript says a challenge phrase.
///
/// THE MEASURE
///     Both sides are normalised (below), then the phrase is aligned against the BEST-MATCHING
///     stretch of the transcript (approximate substring match: free leading and trailing text in
///     the transcript, Levenshtein cost inside it). Similarity is
///     <c>1 - edits / phrase length</c>, clamped to [0, 1].
///
///     Character-level, not word-level, because the three profile languages disagree about what a
///     word is: Japanese has no spaces, and a Vietnamese word is several space-separated
///     syllables that a transcriber may join or split.
///
///     Free surrounding text on purpose: people say "okay" before the phrase or keep talking
///     after it, and the recording that becomes the voice is better for being longer.
///
/// NORMALISATION
///     NFKC, lower case, then every combining mark stripped and đ folded to d, then everything
///     that is not a letter or digit removed. Stripping tone marks forgives a transcriber that
///     hears the right syllable with the wrong tone — the phrase is random, so a near-miss on a
///     diacritic is still overwhelmingly evidence the phrase was read.
///
/// THE THRESHOLD — <see cref="PassThreshold"/> = 0.75
///     Up to a quarter of the phrase's characters may be wrong, which absorbs ordinary
///     transcription noise (a dropped plural, a misheard syllable or two). A recording that says
///     something else scores far below it: unrelated speech against an 8-word random phrase
///     measured 0.2–0.45 in the unit tests. Raise it to be stricter; the cost is more honest
///     readers asked to try again.
/// </summary>
public static class VoiceChallengeMatcher
{
    public const double PassThreshold = 0.75;

    /// <summary>
    /// A transcript longer than this is cut before matching. The alignment is O(phrase x
    /// transcript); a two-minute clip transcribes to well under this.
    /// </summary>
    private const int MaxTranscriptChars = 4000;

    public static bool Passes(double similarity) => similarity >= PassThreshold;

    public static double Similarity(string phrase, string? transcript)
    {
        var p = Normalize(phrase);
        if (p.Length == 0)
        {
            return 0;
        }

        var t = Normalize(transcript ?? string.Empty);
        if (t.Length > MaxTranscriptChars)
        {
            t = t[..MaxTranscriptChars];
        }

        // previous[j]: cost of aligning phrase[..i] so that it ends at transcript position j.
        // Row 0 is all zeros — the phrase may start anywhere in the transcript.
        var previous = new int[t.Length + 1];
        var current = new int[t.Length + 1];
        for (var i = 1; i <= p.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var substitution = previous[j - 1] + (p[i - 1] == t[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }
            (previous, current) = (current, previous);
        }

        // ... and may end anywhere too.
        var best = int.MaxValue;
        foreach (var cost in previous)
        {
            best = Math.Min(best, cost);
        }

        return Math.Clamp(1.0 - (double)best / p.Length, 0.0, 1.0);
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var decomposed = value
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var folded = ch == 'đ' ? 'd' : ch;
            if (char.IsLetterOrDigit(folded))
            {
                builder.Append(folded);
            }
        }

        return builder.ToString();
    }
}

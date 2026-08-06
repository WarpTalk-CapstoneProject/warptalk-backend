using System.Globalization;

namespace WarpTalk.Shared;

/// <summary>
/// The one place that decides what a model-confidence field on a Redis result message means,
/// shared by TranscriptService's persistence consumer and the Gateway's live fan-out so the two
/// can never disagree about whether a value is a measurement or a missing one.
/// </summary>
/// <remarks>
/// WT-277: both consumers used to read confidence as
/// <c>float.TryParse(raw, out var c) ? c : 1.0f</c>. A message that carried no confidence at all
/// therefore persisted as <c>1.0000</c> — the maximum — so "we do not know" became byte-identical
/// to "the model was maximally sure". Confidence is nullable everywhere downstream precisely so
/// that unknown can be stored as unknown; this type is what produces that null.
/// </remarks>
public static class ModelConfidence
{
    /// <summary>
    /// warptalk-ai/stt_worker/model.py uses <c>float(seg.get("avg_logprob", -1.0))</c>: -1.0 is its
    /// explicit "this realtime event exposed no token logprobs" fallback, not a measured score. It
    /// reaches us as an ordinary-looking number, so it has to be recognised here or it silently
    /// becomes real data. A genuine avg_logprob of exactly -1.0 is indistinguishable from the
    /// sentinel on the wire and is deliberately treated as unknown too — dropping one borderline
    /// real measurement is cheaper than storing a fabricated one.
    /// </summary>
    public const double UnknownSentinel = -1.0d;

    /// <summary>
    /// Parses a raw confidence field into a value, or <c>null</c> when the producer did not
    /// actually tell us anything: the field is absent/blank, it is unparsable, it is NaN/infinity,
    /// or it is <see cref="UnknownSentinel"/>.
    /// </summary>
    /// <remarks>
    /// InvariantCulture is required, not cosmetic: the producer always writes "-0.42", and a host
    /// whose current culture uses "," as the decimal separator parses that as -42 (same class of
    /// bug as the billing JSON culture defect). Every warptalk-ai producer serialises with
    /// Python's <c>str(float)</c>, which is always invariant.
    /// </remarks>
    public static decimal? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return null;
        }

        // ReSharper disable once CompareOfFloatsByEqualityOperator — the sentinel is written by the
        // producer as the literal "-1.0" and round-trips exactly through double.
        if (parsed == UnknownSentinel)
        {
            return null;
        }

        return (decimal)parsed;
    }
}

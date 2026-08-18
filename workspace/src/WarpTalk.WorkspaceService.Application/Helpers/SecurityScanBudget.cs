using System;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// How long the ingestion path is willing to wait for the security worker.
/// </summary>
/// <remarks>
/// <para>
/// The wait used to be a flat 30 seconds. That was correct while the scan replied with a verdict
/// — a handful of booleans, produced in about the same time whatever the document. WT-460 changed
/// what the scan returns: to mask PII in place, the model now echoes the ENTIRE analysed text back
/// with the sensitive spans replaced. Generation time therefore scales with the size of the
/// document, and a constant ceiling over a variable amount of work will always cut something off.
/// </para>
/// <para>
/// It did. Measured in production on 2026-08-18, both scans finished correctly and both were
/// thrown away because nobody was still listening:
/// </para>
/// <list type="table">
///   <item><description>27 KB document — worker completed in 67.2s, caller gave up at 30s</description></item>
///   <item><description>33 KB document — worker completed in 53.6s, caller gave up at 30s</description></item>
/// </list>
/// <para>
/// The document was marked <c>security_scan_timeout</c> while the finished, masked result sat
/// unread in Redis until its TTL expired. Nothing was broken and nothing was retried: the scan
/// worked, the answer arrived, and the deadline had already passed.
/// </para>
/// <para>
/// This is the same defect as WT-460 one layer up. There the output budget was fixed while the
/// input varied; here the wait is fixed while the work varies. Both are a constant standing in for
/// something proportional, so the budget is derived from the content instead.
/// </para>
/// </remarks>
public static class SecurityScanBudget
{
    /// <summary>
    /// The floor, and the whole budget for a short document.
    /// </summary>
    /// <remarks>
    /// Deliberately the previous flat value. Small documents were never the problem, so their
    /// behaviour is unchanged and this fix cannot regress them.
    /// </remarks>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ceiling. A scan still hanging at this point is a stuck worker, not a slow one.
    /// </summary>
    /// <remarks>
    /// Without a ceiling a single enormous upload would pin an ingestion consumer for as long as
    /// the arithmetic said, and the consumer processes documents one at a time.
    /// </remarks>
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Seconds allowed per 1,000 characters the model has to reproduce.
    /// </summary>
    /// <remarks>
    /// The two production samples ran at roughly 1.6 s and 2.5 s per 1,000 characters. Six is
    /// therefore about a 2.5x margin over the slower of them, which is the right direction to be
    /// generous in: waiting too long costs one consumer some idle seconds on a rare document,
    /// whereas waiting too little discards a completed scan and hides the document from search
    /// with a reason that blames the wrong component.
    /// </remarks>
    public const double SecondsPerThousandCharacters = 6.0;

    /// <summary>
    /// Characters beyond which more input costs no more time.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>SECURITY_MAX_ANALYZE_LENGTH</c> in warptalk-ai's <c>security_worker/scanners.py</c>,
    /// which truncates before prompting. Past this point the worker is doing exactly the same
    /// amount of work, so a 400 KB PDF must not be granted a budget forty times a 10 KB one for
    /// output it will never generate. If that constant is raised on the worker, raise this with it.
    /// </remarks>
    public const int MaxAnalysedCharacters = 20_000;

    /// <summary>
    /// How long to wait for a scan of <paramref name="contentLength"/> characters.
    /// </summary>
    public static TimeSpan For(int contentLength)
    {
        // Negative or absent content is not an error worth throwing over here; it simply earns
        // the floor, and the scan of an empty document returns almost immediately anyway.
        var billableCharacters = Math.Clamp(contentLength, 0, MaxAnalysedCharacters);
        var derived = Minimum
            + TimeSpan.FromSeconds(billableCharacters / 1000.0 * SecondsPerThousandCharacters);

        return derived > Maximum ? Maximum : derived;
    }
}

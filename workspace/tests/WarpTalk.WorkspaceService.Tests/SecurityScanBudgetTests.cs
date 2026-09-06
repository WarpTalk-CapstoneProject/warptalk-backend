using System;
using WarpTalk.WorkspaceService.Application.Helpers;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The wait allowed for a security scan.
/// </summary>
/// <remarks>
/// These numbers are not invented. Both cases below were measured in production on 2026-08-18,
/// where the worker finished the scan correctly and the caller had already given up — so the
/// document was marked <c>security_scan_timeout</c> while its finished, masked content sat unread
/// in Redis until the TTL took it.
/// </remarks>
public class SecurityScanBudgetTests
{
    [Fact]
    public void For_ShouldCoverTheProductionScanThatTookSixtySevenSeconds()
    {
        // 27,434-byte document. Worker started 03:34:42.26, completed 03:35:49.43 — 67.2s.
        // The caller's flat 30s budget expired at 03:35:12, 37 seconds before the answer arrived.
        var budget = SecurityScanBudget.For(27_434);

        Assert.True(
            budget > TimeSpan.FromSeconds(67.2),
            $"budget {budget.TotalSeconds:0.#}s would have discarded a scan that completed in 67.2s");
    }

    [Fact]
    public void For_ShouldCoverTheProductionScanThatTookFiftyFourSeconds()
    {
        // 33,481-byte document. Worker completed in 53.6s; the caller gave up at 30s.
        var budget = SecurityScanBudget.For(33_481);

        Assert.True(
            budget > TimeSpan.FromSeconds(53.6),
            $"budget {budget.TotalSeconds:0.#}s would have discarded a scan that completed in 53.6s");
    }

    [Fact]
    public void For_ShouldKeepTheOldFlatWaitForSmallDocuments()
    {
        // Small documents never failed, so the fix must not change them at all — otherwise it
        // trades a known bug for an unknown one.
        Assert.Equal(SecurityScanBudget.Minimum, SecurityScanBudget.For(0));
        Assert.True(SecurityScanBudget.For(200) >= SecurityScanBudget.Minimum);
    }

    [Fact]
    public void For_ShouldNeverReturnLessThanTheMinimum()
    {
        foreach (var length in new[] { int.MinValue, -1, 0, 1, 999 })
        {
            Assert.True(
                SecurityScanBudget.For(length) >= SecurityScanBudget.Minimum,
                $"length {length} produced a budget below the floor");
        }
    }

    [Fact]
    public void For_ShouldKeepGrowingBecauseTheWorkerKeepsReading()
    {
        // The mirror image of the assertion that used to stand here. It clamped at 20,000 because
        // the worker truncated there — and budgeting correctly for a scan of the first 6.8% of a
        // document is not a correct budget; it is a correct budget for the wrong scan. The worker
        // reads the whole file now, so a large document is entitled to more time than a small one.
        var small = SecurityScanBudget.For(20_000);
        var large = SecurityScanBudget.For(436_011); // a real Database_Design_Document.pdf

        Assert.True(
            large > small,
            $"a 436k-character document was allowed {large.TotalSeconds:0.#}s, no more than the "
                + $"{small.TotalSeconds:0.#}s given a 20k one");
    }

    [Fact]
    public void For_ShouldCoverTheChunkedScanOfTheFiveSeptemberDocument()
    {
        // 293,950 characters: fifteen 20,000-character chunks, three waves at a concurrency of
        // eight, a wave measured at about 37 seconds in production — roughly 111 seconds of real
        // work. Under the old clamp this document was allowed 150s for a scan of 20,000 of its
        // characters, and the moment the worker began reading all of them that stopped being enough.
        var budget = SecurityScanBudget.For(293_950);

        Assert.True(
            budget > TimeSpan.FromSeconds(111),
            $"budget {budget.TotalSeconds:0.#}s would cut off a chunked scan needing about 111s");
    }

    [Fact]
    public void For_ShouldNeverExceedTheCeiling()
    {
        // A scan still unanswered at the ceiling is a stuck worker, not a slow one, and the
        // ingestion consumer handles one document at a time.
        foreach (var length in new[] { 20_000, 100_000, int.MaxValue })
        {
            Assert.True(
                SecurityScanBudget.For(length) <= SecurityScanBudget.Maximum,
                $"length {length} exceeded the ceiling");
        }
    }

    [Fact]
    public void For_ShouldGrowWithContentLength()
    {
        // The property that matters: the budget tracks the work. A constant here is exactly the
        // defect being fixed, and would still satisfy every bound above on its own.
        var small = SecurityScanBudget.For(1_000);
        var medium = SecurityScanBudget.For(10_000);
        var large = SecurityScanBudget.For(19_000);

        Assert.True(small < medium, "budget did not grow between 1k and 10k characters");
        Assert.True(medium < large, "budget did not grow between 10k and 19k characters");
    }

    [Fact]
    public void MaxScannedCharacters_ShouldMatchTheWorkerTotalScanCap()
    {
        // Mirrors SECURITY_MAX_TOTAL_ANALYZE_LENGTH in warptalk-ai security_worker/scanners.py —
        // the size at which the worker refuses the document outright. Past it the wait would be
        // spent on an answer that is never coming, so the two constants have to agree.
        Assert.Equal(400_000, SecurityScanBudget.MaxScannedCharacters);
    }
}

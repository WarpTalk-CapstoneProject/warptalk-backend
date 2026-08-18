using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

/// <summary>
/// Sends document PII/DLP scans to the security worker through Redis Streams.
/// </summary>
public sealed class DocumentSecurityScanner : IDocumentSecurityScanner
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<DocumentSecurityScanner> _logger;

    public DocumentSecurityScanner(
        IConnectionMultiplexer redis,
        ILogger<DocumentSecurityScanner> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<DocumentSecurityScanResult> ScanAsync(
        string content,
        bool piiEnabled,
        bool dlpEnabled,
        List<string>? keywordsBlacklist,
        CancellationToken ct = default)
    {
        if (!piiEnabled && !dlpEnabled)
        {
            return new DocumentSecurityScanResult(false, false, false, content);
        }

        var database = _redis.GetDatabase();
        var scanId = Guid.NewGuid().ToString("N");
        var resultKey = $"security:scan_result:{scanId}";

        var entries = new NameValueEntry[]
        {
            new("scan_id", scanId),
            new("content", content),
            new("pii_enabled", piiEnabled.ToString().ToLowerInvariant()),
            new("dlp_enabled", dlpEnabled.ToString().ToLowerInvariant()),
            new("keywords", JsonSerializer.Serialize(keywordsBlacklist ?? []))
        };

        // Derived from the document's size rather than fixed: since WT-460 the scan echoes the
        // whole analysed text back with PII masked, so how long it takes depends on how much
        // there is to reproduce. See SecurityScanBudget for the production measurements.
        var budget = SecurityScanBudget.For(content.Length);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await database.StreamAddAsync("security:scan_requests", entries);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget);

            while (!timeout.IsCancellationRequested)
            {
                var value = await database.StringGetAsync(resultKey);
                if (!value.IsNull)
                {
                    await database.KeyDeleteAsync(resultKey);

                    // Logged on the SUCCESS path too. The flat 30s stood for months because
                    // nothing recorded how long a scan actually took, so there was no way to see
                    // the margin shrinking until documents started failing.
                    _logger.LogInformation(
                        "Security scan completed in {ElapsedMs}ms of a {BudgetMs}ms budget for "
                        + "{ContentLength} characters. ScanId: {ScanId}",
                        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                        (long)budget.TotalMilliseconds,
                        content.Length,
                        scanId);

                    var response = JsonSerializer.Deserialize<ScanResponse>((string)value!);
                    if (response is null)
                    {
                        throw new InvalidOperationException(
                            "Security worker returned an invalid response.");
                    }
                    if (response.ScanFailed)
                    {
                        throw new InvalidOperationException(
                            "Security worker could not complete the requested scan.");
                    }

                    return new DocumentSecurityScanResult(
                        response.ViolationFound,
                        response.PiiDetected,
                        response.DlpDetected,
                        string.IsNullOrWhiteSpace(response.MaskedContent)
                            ? content
                            : response.MaskedContent);
                }

                await Task.Delay(500, timeout.Token);
            }

            throw TimedOut(scanId, content.Length, budget);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(scanId, content.Length, budget);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Security scan failed closed. ScanId: {ScanId}",
                scanId);
            throw;
        }
    }

    /// <summary>
    /// The timeout, carrying enough to diagnose it without another production investigation.
    /// </summary>
    /// <remarks>
    /// The old message was the scan id alone. Recovering what actually happened meant correlating
    /// that id against the worker's logs by hand — and since a deploy recreates the worker and
    /// takes its logs with it, that evidence is routinely gone. The size and the budget are what
    /// distinguish the two very different causes: a budget the document outgrew, or a worker that
    /// never answered at all.
    /// </remarks>
    private static TimeoutException TimedOut(string scanId, int contentLength, TimeSpan budget) =>
        new($"Security scan timed out after {budget.TotalSeconds:0.#}s for {contentLength} "
            + $"characters. ScanId: {scanId}");

    private sealed class ScanResponse
    {
        [JsonPropertyName("pii_detected")]
        public bool PiiDetected { get; init; }

        [JsonPropertyName("dlp_detected")]
        public bool DlpDetected { get; init; }

        [JsonPropertyName("violation_found")]
        public bool ViolationFound { get; init; }

        [JsonPropertyName("masked_content")]
        public string? MaskedContent { get; init; }

        [JsonPropertyName("scan_failed")]
        public bool ScanFailed { get; init; }
    }
}

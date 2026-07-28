using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Services;

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

        try
        {
            await database.StreamAddAsync("security:scan_requests", entries);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            while (!timeout.IsCancellationRequested)
            {
                var value = await database.StringGetAsync(resultKey);
                if (!value.IsNull)
                {
                    await database.KeyDeleteAsync(resultKey);
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

            throw new TimeoutException($"Security scan timed out. ScanId: {scanId}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Security scan timed out. ScanId: {scanId}");
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

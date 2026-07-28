
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Services;

/// <summary>
/// Infrastructure service performing OpenAI-based PII scans, multi-language PII masking, and DLP checks on raw text via Redis Streams.
/// </summary>
public class DocumentSecurityScanner : IDocumentSecurityScanner
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

    public async Task<DocumentSecurityScanResult> ScanAsync(string content, bool piiEnabled, bool dlpEnabled, List<string>? keywordsBlacklist, CancellationToken ct = default)
    {
        if (!piiEnabled && !dlpEnabled)
        {
            return new DocumentSecurityScanResult(false, false, false, content);
        }

        var db = _redis.GetDatabase();
        var scanId = Guid.NewGuid().ToString("N");
        var streamKey = "security:scan_requests";
        var resultKey = $"security:scan_result:{scanId}";

        var keywordsJson = keywordsBlacklist != null ? JsonSerializer.Serialize(keywordsBlacklist) : "[]";

        var entries = new NameValueEntry[]
        {
            new("scan_id", scanId),
            new("content", content),
            new("pii_enabled", piiEnabled.ToString().ToLower()),
            new("dlp_enabled", dlpEnabled.ToString().ToLower()),
            new("keywords", keywordsJson)
        };

        _logger.LogInformation("Publishing security scan request to Redis. ScanId: {ScanId}", scanId);

        try
        {
            await db.StreamAddAsync(streamKey, entries);

            var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutToken.CancelAfter(TimeSpan.FromSeconds(30));

            while (!timeoutToken.IsCancellationRequested)
            {
                var value = await db.StringGetAsync(resultKey);
                if (!value.IsNull)
                {
                    _logger.LogInformation("Security scan result received for ScanId: {ScanId}", scanId);
                    var result = JsonSerializer.Deserialize<ScanResponse>((string)value!);
                    
                    await db.KeyDeleteAsync(resultKey);

                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to deserialize security scan response from Redis.");
                    }

                    var maskedContent = !string.IsNullOrWhiteSpace(result.MaskedContent) ? result.MaskedContent : content;
                    return new DocumentSecurityScanResult(result.ViolationFound, result.PiiDetected, result.DlpDetected, maskedContent);
                }

                await Task.Delay(500, ct);
            }

            throw new TimeoutException($"Security scan request timed out. ScanId: {scanId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing remote security scan via Redis. ScanId: {ScanId}", scanId);
            throw; // Trigger fail-closed policy
        }
    }

    private class ScanResponse
    {
        [JsonPropertyName("pii_detected")]
        public bool PiiDetected { get; set; }

        [JsonPropertyName("dlp_detected")]
        public bool DlpDetected { get; set; }

        [JsonPropertyName("violation_found")]
        public bool ViolationFound { get; set; }

        [JsonPropertyName("masked_content")]
        public string? MaskedContent { get; set; }
    }
}

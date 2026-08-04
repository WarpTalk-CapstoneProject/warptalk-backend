using System.Text.Json.Serialization;

namespace WarpTalk.WorkspaceService.Infrastructure.Models;

/// <summary>
/// Redis payload response model for remote document security scanning.
/// </summary>
public record DocumentSecurityRedisScanResponse(
    [property: JsonPropertyName("pii_detected")] bool PiiDetected,
    [property: JsonPropertyName("dlp_detected")] bool DlpDetected,
    [property: JsonPropertyName("violation_found")] bool ViolationFound,
    [property: JsonPropertyName("masked_content")] string? MaskedContent
);

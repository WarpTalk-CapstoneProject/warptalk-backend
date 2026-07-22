using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Service contract for scanning extracted document content for sensitive data violations.
/// </summary>
public interface IDocumentSecurityScanner
{
    Task<DocumentSecurityScanResult> ScanAsync(string content, bool piiEnabled, bool dlpEnabled, List<string>? keywordsBlacklist, CancellationToken ct = default);
}

/// <summary>
/// Model representing the results of a security scan.
/// </summary>
public record DocumentSecurityScanResult(
    bool ViolationFound,
    bool PiiDetected,
    bool DlpDetected
);

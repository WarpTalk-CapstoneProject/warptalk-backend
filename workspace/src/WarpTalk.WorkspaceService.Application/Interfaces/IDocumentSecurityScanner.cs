using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Service contract for scanning extracted document content for sensitive data violations.
/// </summary>
public interface IDocumentSecurityScanner
{
    DocumentSecurityScanResult Scan(string content, bool piiEnabled, bool dlpEnabled, List<string>? keywordsBlacklist);
}

/// <summary>
/// Model representing the results of a security scan.
/// </summary>
public record DocumentSecurityScanResult(
    bool ViolationFound,
    bool PiiDetected,
    bool DlpDetected
);

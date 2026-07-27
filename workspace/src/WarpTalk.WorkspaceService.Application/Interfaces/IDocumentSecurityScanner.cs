using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public record DocumentSecurityScanResult(
    bool ViolationFound,
    bool PiiDetected,
    bool DlpDetected,
    string? MaskedContent = null);

public interface IDocumentSecurityScanner
{
    Task<DocumentSecurityScanResult> ScanAsync(
        string content,
        bool piiEnabled,
        bool dlpEnabled,
        List<string>? keywordsBlacklist,
        CancellationToken ct = default);
}

using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Produces, verifies and stores the PII-masked copy of a document's current file.
/// </summary>
/// <remarks>
/// Called by the security guardrail once a scan has answered, and only there: the masked copy is
/// a product of the scan, not something a request can ask for directly.
///
/// The contract is fail-closed. Whatever goes wrong — the hidden values cannot be located, the
/// rewritten file still shows one of them, the store refuses — the result is that NO masked copy
/// exists afterwards, including any copy an earlier scan left behind. It never throws for those
/// reasons; it returns a status from
/// <see cref="Domain.Constants.WorkspaceDocumentMaskedVersionStatuses"/> saying which.
/// </remarks>
public interface IDocumentMaskedVersionGenerator
{
    Task<string> GenerateAsync(
        WorkspaceDocument document,
        byte[] originalFile,
        ExtractedDocumentContent extracted,
        DocumentSecurityScanResult scanResult,
        CancellationToken ct = default);

    /// <summary>
    /// Removes any masked copy of the current file. Used when a scan did not get far enough to
    /// say what the copy should contain.
    /// </summary>
    Task DiscardAsync(WorkspaceDocument document, CancellationToken ct = default);
}

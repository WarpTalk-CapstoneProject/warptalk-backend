using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Helpers;

public static class DocumentSecurityGuardrailHelper
{
    /// <summary>
    /// Kept as the name this service's consumers already call; the rule itself now lives in
    /// <see cref="WorkspaceDocumentExtensions.IsIndexEligible"/> so Application can ask the same
    /// question. Two copies of an index gate is how one of them ends up missing a condition.
    /// </summary>
    public static bool HasBasicIndexEligibility(WorkspaceDocument document)
        => document.IsIndexEligible();

    /// <summary>
    /// Has an Owner/Admin asked for this document's masked copy to be produced, with no scan
    /// having answered since?
    /// </summary>
    /// <remarks>
    /// The request is an audit row rather than a field on the event, so the ingestion event's
    /// contract is untouched and the request survives a consumer restart: it stays pending until
    /// a scan (or a recorded failure) is newer than it.
    /// </remarks>
    public static async Task<bool> HasPendingMaskedVersionRescanAsync(
        IUnitOfWork unitOfWork,
        Guid documentId,
        CancellationToken ct)
    {
        var audits = unitOfWork.WorkspaceDocumentAuditRepository;
        var requested = await audits.GetLatestActionAsync(
            documentId, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanRequested, ct);
        if (requested is null)
        {
            return false;
        }

        var scanned = await audits.GetLatestActionAsync(
            documentId, WorkspaceDocumentConstants.AuditActions.SecurityScanCompleted, ct);
        var failed = await audits.GetLatestActionAsync(
            documentId, WorkspaceDocumentConstants.AuditActions.MaskedVersionRescanFailed, ct);

        return (scanned is null || scanned.ActionAt < requested.ActionAt)
            && (failed is null || failed.ActionAt < requested.ActionAt);
    }

    public static async Task MarkSkippedAsync(
        WorkspaceDocument document,
        IUnitOfWork unitOfWork,
        IWorkspaceDocumentEventPublisher lifecyclePublisher,
        CancellationToken ct)
    {
        document.AiEligible = false;
        document.IngestionStatus = WorkspaceDocumentIngestionStatus.skipped.ToString();
        document.UpdatedAt = DateTime.UtcNow;
        unitOfWork.WorkspaceDocumentRepository.Update(document);
        await unitOfWork.SaveChangesAsync(ct);
        await lifecyclePublisher.PublishDocumentLifecycleAsync(
            document.Id,
            document.WorkspaceId,
            document.Status,
            document.IngestionStatus,
            WorkspaceDocumentConstants.LifecycleEvents.Updated,
            document.UpdatedAt,
            document.UploadedBy,
            ct);
    }
}

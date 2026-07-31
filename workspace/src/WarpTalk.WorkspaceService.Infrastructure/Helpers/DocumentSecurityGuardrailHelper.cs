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
    public static bool HasBasicIndexEligibility(WorkspaceDocument document)
    {
        return document.IsAiAllowed
            && string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(document.RetentionState, "active", StringComparison.OrdinalIgnoreCase)
            && !document.IsRestricted();
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

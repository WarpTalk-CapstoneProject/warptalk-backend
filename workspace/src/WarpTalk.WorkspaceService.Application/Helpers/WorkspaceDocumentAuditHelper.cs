using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceDocumentAuditHelper
{
    public static async Task AuditAsync(
        this IUnitOfWork unitOfWork,
        Guid documentId,
        Guid workspaceId,
        Guid? actorId,
        string action,
        object? metadata = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            var audit = WorkspaceDocumentMapper.ToAuditEntity(documentId, workspaceId, actorId, action, metadata);
            await unitOfWork.WorkspaceDocumentAuditRepository.AddAsync(audit, ct);
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to write document audit log. DocumentId: {DocumentId}, Action: {Action}", documentId, action);
        }
    }
}

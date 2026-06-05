using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceDocumentMapper
{
    public static WorkspaceDocumentDto ToDto(this WorkspaceDocument doc, IWorkspaceUrlProvider urlProvider)
    {
        return new WorkspaceDocumentDto(
            doc.Id,
            doc.WorkspaceId,
            doc.UploadedBy,
            doc.OwnerId,
            doc.Name,
            doc.FileName,
            doc.FileExtension,
            doc.MimeType,
            doc.SizeBytes,
            doc.SourceType,
            doc.SourceId,
            doc.IngestionStatus,
            doc.IsSensitive,
            doc.ConfidentialityLevel,
            doc.RetentionState,
            doc.Status,
            urlProvider.GetDocumentDownloadUrl(doc.WorkspaceId, doc.Id),
            doc.CreatedAt,
            doc.UpdatedAt
        );
    }

    public static WorkspaceDocument ToEntity(
        this UploadDocumentRequest request,
        Guid docId,
        Guid workspaceId,
        Guid userId,
        string storageKey,
        bool isOwnerOrAdmin)
    {
        return new WorkspaceDocument
        {
            Id = docId,
            WorkspaceId = workspaceId,
            UploadedBy = userId,
            OwnerId = userId,
            Name = request.Name,
            FileName = request.FileName,
            FileExtension = request.FileExtension,
            MimeType = request.MimeType,
            SizeBytes = request.SizeBytes,
            StorageProvider = WorkspaceDocumentConstants.LocalStorageProvider,
            StorageKey = storageKey,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            DocumentType = request.FileExtension.TrimStart('.').ToUpper(),
            AiEligible = isOwnerOrAdmin,
            IngestionStatus = isOwnerOrAdmin 
                ? WorkspaceDocumentIngestionStatus.pending.ToString() 
                : WorkspaceDocumentIngestionStatus.awaiting_approval.ToString(),
            IsSensitive = request.IsSensitive,
            ConfidentialityLevel = WorkspaceDocumentHelper.GetConfidentialityLevel(request.IsSensitive),
            RetentionState = WorkspaceDocumentConstants.RetentionStateActive,
            Status = isOwnerOrAdmin 
                ? WorkspaceDocumentStatus.active.ToString() 
                : WorkspaceDocumentStatus.pending_approval.ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}

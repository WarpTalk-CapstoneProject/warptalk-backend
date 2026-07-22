using System;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceDocumentMapper
{
    public static WorkspaceDocumentDto ToDto(this WorkspaceDocument doc, string downloadUrl)
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
            doc.AiEligible,
            doc.IsAiAllowed,
            doc.IsSensitive,
            doc.ConfidentialityLevel,
            doc.RetentionState,
            doc.Status,
            downloadUrl,
            doc.CreatedAt,
            doc.UpdatedAt
        );
    }

    public static WorkspaceDocument ToEntity(
        this UploadDocumentApiRequest request,
        Guid docId,
        Guid workspaceId,
        Guid userId,
        string storageKey,
        WorkspaceDocumentStatus status,
        WorkspaceDocumentIngestionStatus ingestionStatus,
        bool aiEligible,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var extension = System.IO.Path.GetExtension(request.File.FileName);
        return new WorkspaceDocument
        {
            Id = docId,
            WorkspaceId = workspaceId,
            UploadedBy = userId,
            OwnerId = userId,
            Name = request.Name,
            FileName = request.File.FileName,
            FileExtension = extension,
            MimeType = request.File.ContentType,
            SizeBytes = request.File.Length,
            StorageProvider = WorkspaceDocumentConstants.LocalStorageProvider,
            StorageKey = storageKey,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            DocumentType = extension.TrimStart('.').ToUpper(),
            AiEligible = aiEligible,
            IsAiAllowed = request.IsAiAllowed,
            IngestionStatus = ingestionStatus.ToString(),
            IsSensitive = request.IsSensitive,
            ConfidentialityLevel = WorkspaceDocumentHelper.GetConfidentialityLevel(request.IsSensitive),
            RetentionState = WorkspaceDocumentConstants.RetentionStateActive,
            Status = status.ToString(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static WorkspaceDocumentAudit ToAuditEntity(
        Guid documentId,
        Guid workspaceId,
        Guid? actorId,
        string action,
        object? metadata = null,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceDocumentAudit
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            WorkspaceId = workspaceId,
            ActorId = actorId,
            Action = action,
            ActionAt = now,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata) : null
        };
    }
}

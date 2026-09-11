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
    public static WorkspaceDocumentDto ToDto(
        this WorkspaceDocument doc,
        string downloadUrl,
        Guid? approvedBy = null,
        string? rejectionReason = null)
    {
        return new WorkspaceDocumentDto(
            doc.Id,
            doc.WorkspaceId,
            doc.UploadedBy,
            approvedBy,
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
            doc.ConfidentialityLevel,
            doc.RetentionState,
            doc.Status,
            downloadUrl,
            doc.CreatedAt,
            doc.UpdatedAt,
            doc.IngestionFailureReason,
            rejectionReason
        );
    }

    public static WorkspaceDocument ToEntity(
        this UploadDocumentApiRequest request,
        Guid docId,
        Guid workspaceId,
        Guid userId,
        string storageKey,
        string storageProvider,
        WorkspaceDocumentStatus status,
        WorkspaceDocumentIngestionStatus ingestionStatus,
        bool aiEligible,
        DateTime? utcNow = null,
        string? contentHash = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var extension = WorkspaceDocumentHelper.NormalizeExtension(System.IO.Path.GetExtension(request.File.FileName));
        return new WorkspaceDocument
        {
            Id = docId,
            WorkspaceId = workspaceId,
            UploadedBy = userId,
            OwnerId = userId,
            Name = request.Name,
            ContentHash = contentHash,
            FileName = request.File.FileName,
            FileExtension = extension,
            MimeType = WorkspaceDocumentHelper.GetSafeContentType(extension),
            SizeBytes = request.File.Length,
            StorageProvider = storageProvider,
            StorageKey = storageKey,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            DocumentType = extension.TrimStart('.').ToUpper(),
            AiEligible = aiEligible,
            IsAiAllowed = request.IsAiAllowed,
            IngestionStatus = ingestionStatus.ToString(),
            ConfidentialityLevel = string.IsNullOrWhiteSpace(request.ConfidentialityLevel) ? WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel : request.ConfidentialityLevel,
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

    /// <summary>
    /// The name the reject and re-upload paths write their free text under, inside the audit row's
    /// Metadata JSON.
    /// </summary>
    /// <remarks>
    /// One constant, read by <see cref="ToHistoryDto"/> and by the detail route's rejection-reason
    /// lookup, written by both producers. Metadata is an anonymous object serialised by
    /// <see cref="ToAuditEntity"/>, so a property name is the only contract there is — two spellings
    /// would mean a reason that is stored and never read, which is the exact failure WT-633 is
    /// about.
    /// </remarks>
    public const string AuditReasonProperty = "reason";

    /// <summary>
    /// One audit row as a history entry, with the reviewer's or uploader's words lifted out of the
    /// Metadata JSON. WT-633.
    /// </summary>
    /// <remarks>
    /// Metadata is free-form across thirteen actions — upload writes a name and a confidentiality
    /// level, policy changes write a subject and an effect — so this reads ONE optional property
    /// and ignores everything else. A row whose metadata is absent, malformed, or shaped like
    /// something other than an object yields a null reason rather than throwing: the history of a
    /// document is not worth a 500.
    /// </remarks>
    public static DocumentHistoryEntryDto ToHistoryDto(this WorkspaceDocumentAudit audit)
    {
        return new DocumentHistoryEntryDto(
            audit.Id,
            audit.Action,
            audit.ActorId,
            audit.ActionAt,
            ReadAuditReason(audit.Metadata));
    }

    /// <summary>The `reason` string inside an audit row's Metadata JSON, or null.</summary>
    public static string? ReadAuditReason(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            using var parsed = JsonDocument.Parse(metadata);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!parsed.RootElement.TryGetProperty(AuditReasonProperty, out var reason)
                || reason.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = reason.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceDocumentHelper
{
    // Helper method to get confidentiality level based on sensitivity
    public static string GetConfidentialityLevel(bool isSensitive)
    {
        return isSensitive
            ? WorkspaceDocumentConstants.SensitiveConfidentialityLevel
            : WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;
    }

    // Helper method to generate the storage key for a document
    public static string GenerateStorageKey(Guid workspaceId, Guid documentId, string fileExtension)
    {
        var normalizedExtension = NormalizeExtension(fileExtension);
        return $"documents/{workspaceId}/{documentId}{normalizedExtension}";
    }

    public static string NormalizeExtension(string? fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            return string.Empty;
        }

        var extension = fileExtension.Trim().ToLowerInvariant();
        return extension.StartsWith('.') ? extension : $".{extension}";
    }

    public static bool IsSupportedUploadExtension(string? fileExtension)
    {
        var extension = NormalizeExtension(fileExtension);
        return WorkspaceDocumentConstants.SupportedUploadExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsImageExtension(string? fileExtension)
    {
        var extension = NormalizeExtension(fileExtension);
        return WorkspaceDocumentConstants.ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAiReadableExtension(string? fileExtension)
    {
        var extension = NormalizeExtension(fileExtension);
        return WorkspaceDocumentConstants.AiReadableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetSafeContentType(string? fileExtension)
    {
        var extension = NormalizeExtension(fileExtension);
        return WorkspaceDocumentConstants.ContentTypesByExtension.TryGetValue(extension, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    public static string? NormalizePolicySubjectType(string? subjectType)
    {
        if (string.Equals(subjectType, WorkspacePolicyConstants.SubjectTypeUser, StringComparison.OrdinalIgnoreCase))
            return WorkspacePolicyConstants.SubjectTypeUser;
        if (string.Equals(subjectType, WorkspacePolicyConstants.SubjectTypeRole, StringComparison.OrdinalIgnoreCase))
            return WorkspacePolicyConstants.SubjectTypeRole;
        if (string.Equals(subjectType, WorkspacePolicyConstants.SubjectTypeMembershipType, StringComparison.OrdinalIgnoreCase))
            return WorkspacePolicyConstants.SubjectTypeMembershipType;
        return null;
    }

    public static bool IsSupportedPolicyPermission(string? permission)
    {
        return string.Equals(permission, WorkspaceDocumentPermissions.View, StringComparison.Ordinal)
            || string.Equals(permission, WorkspaceDocumentPermissions.Download, StringComparison.Ordinal)
            || string.Equals(permission, WorkspaceDocumentPermissions.AiRetrieval, StringComparison.Ordinal);
    }
}

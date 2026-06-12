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
        var normalizedExtension = fileExtension.StartsWith('.') ? fileExtension : $".{fileExtension}";
        return $"documents/{workspaceId}/{documentId}{normalizedExtension}";
    }
}

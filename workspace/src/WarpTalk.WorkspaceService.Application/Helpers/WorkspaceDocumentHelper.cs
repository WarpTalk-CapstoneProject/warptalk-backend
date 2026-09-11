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

    /// <summary>
    /// The canonical confidentiality level for a caller-supplied value, or null when the value is
    /// not one this system recognises.
    ///
    /// WHY THIS HAS TO EXIST
    ///     `ConfidentialityLevel` is a free-text column, and both write paths — upload and PATCH —
    ///     stored whatever arrived. Every READER, though, asks exactly one question:
    ///     <c>WorkspaceDocumentExtensions.IsRestricted</c>, which is an equality test against the
    ///     literal "restricted".
    ///
    ///     So a document labelled "confidential", "secret", "internal-only" — or "restricted "
    ///     with one trailing space — is stored as the caller asked, displayed as the caller
    ///     asked, and read by every policy check as NOT restricted. It then passes
    ///     `DocumentSecurityGuardrailHelper.HasBasicIndexEligibility`, is embedded into the vector
    ///     store, and becomes answerable by the assistant. The label says confidential and the
    ///     boundary does not exist.
    ///
    ///     Trimmed and lower-cased before comparing, so a value that differs only in whitespace or
    ///     case is accepted as the level it plainly means rather than silently downgraded.
    ///     Anything else is REFUSED at the boundary rather than normalised to a default: guessing
    ///     that "secret" meant public_internal would be the same failure with better manners, and
    ///     guessing it meant restricted would let a typo lock a document nobody can unlock.
    /// </summary>
    public static string? NormalizeConfidentialityLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim().ToLowerInvariant();

        if (normalized == WorkspaceDocumentConstants.SensitiveConfidentialityLevel)
            return WorkspaceDocumentConstants.SensitiveConfidentialityLevel;

        if (normalized == WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel)
            return WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;

        return null;
    }

    /// <summary>The two values a document may carry, for an error message that names them.</summary>
    public static string SupportedConfidentialityLevels =>
        $"{WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel}, {WorkspaceDocumentConstants.SensitiveConfidentialityLevel}";

    /// <summary>
    /// The canonical source type for a caller-supplied value, or null when it is not one this
    /// system recognises. WT-666.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="NormalizeConfidentialityLevel"/> and for the same reason: the
    /// column was free text, every reader is an equality test, and an unrecognised value is
    /// therefore a document no reader will ever match — invisible to the External-member meeting
    /// exception, and meaningless in the API response.
    ///
    /// Trimmed and lower-cased rather than refused on case: "Upload" plainly means upload.
    /// </remarks>
    public static string? NormalizeSourceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim().ToLowerInvariant();
        return WorkspaceDocumentConstants.SupportedSourceTypes.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    /// <summary>The source types a document may carry, for an error message that names them.</summary>
    public static string SupportedSourceTypes =>
        string.Join(", ", WorkspaceDocumentConstants.SupportedSourceTypes);

    /// <summary>
    /// The canonical duplicate-handling strategy, or null when the caller asked for one that does
    /// not exist. Absent means Reject — the safe default, which asks rather than assumes.
    /// </summary>
    public static string? NormalizeDuplicateStrategy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return WorkspaceDocumentConstants.DuplicateStrategies.Reject;

        var normalized = value.Trim().ToLowerInvariant();
        return WorkspaceDocumentConstants.DuplicateStrategies.All.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    /// <summary>The duplicate strategies a caller may ask for, for an error message that names them.</summary>
    public static string SupportedDuplicateStrategies =>
        string.Join(", ", WorkspaceDocumentConstants.DuplicateStrategies.All);

    // Helper method to generate the storage key for a document
    public static string GenerateStorageKey(Guid workspaceId, Guid documentId, string fileExtension)
    {
        var normalizedExtension = NormalizeExtension(fileExtension);
        return $"documents/{workspaceId}/{documentId}{normalizedExtension}";
    }

    /// <summary>
    /// A storage key for one REVISION of a document, so re-uploading never overwrites the blob a
    /// reviewer already read. WT-633.
    /// </summary>
    /// <remarks>
    /// The plain key is <c>documents/{workspace}/{document}{ext}</c> — one path per document id.
    /// Re-upload keeps the document id on purpose, so reusing that key would encrypt the new file
    /// over the old one and the `previousStorageKey` written into the audit row would point at
    /// bytes that no longer exist. A revision suffix keeps the superseded file addressable, which
    /// is the whole of the ticket's "do not delete the physical file".
    /// </remarks>
    public static string GenerateRevisionStorageKey(Guid workspaceId, Guid documentId, string fileExtension, DateTime utcNow)
    {
        var normalizedExtension = NormalizeExtension(fileExtension);
        return $"documents/{workspaceId}/{documentId}-r{utcNow:yyyyMMddHHmmssfff}{normalizedExtension}";
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

using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceDocumentExtensions
{
    public static bool IsRestricted(this WorkspaceDocument document)
    {
        return string.Equals(
            document.ConfidentialityLevel,
            WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this document published to the whole workspace?
    /// </summary>
    /// <remarks>
    /// `active` is the column's DDL default and predates the enum; the evaluator has always read
    /// it as published, so this does too.
    /// </remarks>
    public static bool IsPublic(this WorkspaceDocument document)
    {
        return string.Equals(document.Status, WorkspaceDocumentStatus.@public.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(document.Status, LegacyPublishedStatus, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Was this document taken back from the workspace after being published?
    /// </summary>
    public static bool IsPrivate(this WorkspaceDocument document)
    {
        return string.Equals(document.Status, WorkspaceDocumentStatus.@private.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The status the DDL default writes; read as published everywhere.</summary>
    public const string LegacyPublishedStatus = "active";

    /// <summary>
    /// May this document's text be placed in the vector index?
    /// </summary>
    /// <remarks>
    /// THE ONE DEFINITION, and it lives in Domain because three layers need it.
    ///
    /// It used to live only in DocumentSecurityGuardrailHelper.HasBasicIndexEligibility, in
    /// Infrastructure — which Application cannot reference. So the one other place that publishes
    /// an index request from Application (UpdateExtractedTextAsync) could not call it and grew its
    /// own shorter copy: IsAiAllowed + status. That copy was missing the retention state AND the
    /// restricted check, so PUT extracted-text re-indexed a document the workspace had declared
    /// confidential — walking straight through the boundary this PR builds elsewhere.
    ///
    /// Four conditions, and each one is a different question:
    ///   IsAiAllowed      — the uploader's own switch: may the model read this document at all
    ///   Status           — has the workspace approved it (pending_approval is not published)
    ///   RetentionState   — is it still live, rather than staged for deletion
    ///   !IsRestricted()  — has it been labelled confidential
    ///
    /// Anything that hands document text to the embedding pipeline asks THIS, not a subset of it.
    /// </remarks>
    public static bool IsIndexEligible(this WorkspaceDocument document)
    {
        return document.IsAiAllowed
            && string.Equals(
                document.Status,
                WorkspaceDocumentStatus.@public.ToString(),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                document.RetentionState,
                WorkspaceDocumentConstants.RetentionStateActive,
                StringComparison.OrdinalIgnoreCase)
            && !document.IsRestricted();
    }
}

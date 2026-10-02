namespace WarpTalk.WorkspaceService.Domain.Constants;

/// <summary>
/// Why a document does or does not have a PII-masked copy. Written into the
/// <c>SecurityScanCompleted</c> audit row by the guardrail, read back by the detail route.
/// </summary>
/// <remarks>
/// A masked copy is the same file with the personal details the scan found replaced by markers.
/// It is what an ordinary member is shown for a document the scan restricted. Whether one EXISTS
/// is answered by storage and by nothing else; this value only explains the answer, so an
/// Owner/Admin looking at a restricted document with no masked copy can see what happened
/// instead of an unexplained empty state.
///
/// No value here ever carries document content.
/// </remarks>
public static class WorkspaceDocumentMaskedVersionStatuses
{
    /// <summary>A masked copy was produced, verified and stored.</summary>
    public const string Available = "available";

    /// <summary>No scan on record says anything about a masked copy (documents from before this existed).</summary>
    public const string NotGenerated = "not_generated";

    /// <summary>An Owner/Admin asked for a re-scan and its result is not in yet.</summary>
    public const string Pending = "pending";

    /// <summary>The scan found no personal details, so there is nothing to hide.</summary>
    public const string NoPiiFound = "no_pii_found";

    /// <summary>The document holds a banned keyword. Hiding personal details would not make it shareable.</summary>
    public const string DlpBlocked = "dlp_blocked";

    /// <summary>The scan reported personal details but did not return text with them hidden.</summary>
    public const string MaskedTextUnavailable = "masked_text_unavailable";

    /// <summary>Images and other formats whose content cannot be rewritten.</summary>
    public const string UnsupportedFormat = "unsupported_format";

    /// <summary>The file holds something the masker cannot safely rewrite (a pivot cache, a macro project…).</summary>
    public const string UnsupportedContent = "unsupported_content";

    /// <summary>The hidden values could not be located in the original text.</summary>
    public const string AlignmentFailed = "alignment_failed";

    /// <summary>A masked copy was produced but still contained something it should have hidden. Not stored.</summary>
    public const string VerificationFailed = "verification_failed";

    /// <summary>PDF only: the converter that renders the masked text is not configured or did not answer.</summary>
    public const string ConverterUnavailable = "converter_unavailable";

    /// <summary>The scan, the rewrite or the store failed.</summary>
    public const string Error = "error";
}

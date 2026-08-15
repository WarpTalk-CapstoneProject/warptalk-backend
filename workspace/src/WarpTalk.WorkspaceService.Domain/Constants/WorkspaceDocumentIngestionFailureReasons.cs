namespace WarpTalk.WorkspaceService.Domain.Constants;

/// <summary>
/// Why an AI ingestion attempt did not complete, stored on
/// <c>workspace_documents.ingestion_failure_reason</c>.
///
/// WT-411. `ingestion_status='failed'` said only that something went wrong, and the fail-safe
/// marked the document restricted at the same time — so a document hidden by a Redis blip and one
/// hidden because it genuinely contains PII were the same row. Nobody could tell whether a retry
/// would help, and nobody could tell an outage from a policy decision.
///
/// Deliberately coarse. These are the branches that actually exist in the guardrail, not a
/// taxonomy invented ahead of the code.
/// </summary>
public static class WorkspaceDocumentIngestionFailureReasons
{
    /// <summary>The scan itself did not answer — timeout, transport error, extraction crash.
    /// Says NOTHING about the content, and is the case a retry can fix.</summary>
    public const string SecurityScanFailed = "security_scan_failed";

    /// <summary>The scan answered and found DLP-listed content. A policy decision, not a fault.</summary>
    public const string DlpDetected = "dlp_detected";

    /// <summary>PII was found but no masked variant came back, so there was nothing safe to index.</summary>
    public const string PiiUnmasked = "pii_unmasked";

    /// <summary>The embedding request could not be published. Retryable.</summary>
    public const string EmbeddingPublishFailed = "embedding_publish_failed";
}

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
    /// <summary>The scan never answered within the 30s window. Says NOTHING about the content.
    /// Points at the security worker or the queue between us and it — a retry may well work.</summary>
    public const string SecurityScanTimeout = "security_scan_timeout";

    /// <summary>The security worker answered and reported it could not complete the scan
    /// (scan_failed=true) — typically its own upstream, e.g. the OpenAI call. Also says nothing
    /// about the content, but points at a DIFFERENT component than a timeout does.</summary>
    public const string SecurityScanFailed = "security_scan_failed";

    /// <summary>Anything else thrown on the ingestion path — extraction, storage, serialisation.
    /// The catch-all, kept last so the specific reasons above are never reached for.</summary>
    public const string IngestionError = "ingestion_error";

    /// <summary>The scan answered and found DLP-listed content. A policy decision, not a fault.</summary>
    public const string DlpDetected = "dlp_detected";

    /// <summary>PII was found but no masked variant came back, so there was nothing safe to index.</summary>
    public const string PiiUnmasked = "pii_unmasked";

    /// <summary>The embedding request could not be published. Retryable.</summary>
    public const string EmbeddingPublishFailed = "embedding_publish_failed";

    /// <summary>
    /// The embedding worker ran and reported that indexing failed.
    ///
    /// The request was published and consumed, so this points downstream of us — the worker's
    /// own model call or its Qdrant upsert — not at the guardrail. WT-411 gave every OTHER
    /// branch a reason and left this one writing NULL, which is why six production documents
    /// read "AI Failed" with nothing anywhere saying why.
    /// </summary>
    public const string EmbeddingFailed = "embedding_failed";

    /// <summary>
    /// The embedding worker refused the document — a policy answer, not a fault, and therefore
    /// not something a retry changes.
    /// </summary>
    public const string EmbeddingBlocked = "embedding_blocked";
}

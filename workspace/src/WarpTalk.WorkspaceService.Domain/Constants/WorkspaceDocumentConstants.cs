namespace WarpTalk.WorkspaceService.Domain.Constants;

public static class WorkspaceDocumentConstants
{
    public const string SensitiveConfidentialityLevel = "restricted";
    public const string NonSensitiveConfidentialityLevel = "public_internal";
    public const string RetentionStateActive = "active";

    /// <summary>
    /// How many AI-retrievable document ids one lookup returns by default.
    /// </summary>
    /// <remarks>
    /// Matches MAX_SCOPED_ROOM_IDS in the assistant worker. Every id travels through a Redis
    /// stream field and into a Qdrant MatchAny, so the list is bounded on purpose at both ends.
    /// </remarks>
    public const int DefaultAiRetrievableIdLimit = 200;

    public const int MaxAiRetrievableIdLimit = 500;
    public const string SourceTypeMeeting = "meeting";
    public const string SourceTypeUpload = "upload";
    public const string LocalStorageProvider = "local";

    /// <summary>
    /// The provenance values a document row may carry.
    /// </summary>
    /// <remarks>
    /// `source_type` was free text straight from the multipart form, so a caller could store any
    /// word they liked and nothing downstream would ever recognise it.
    ///
    /// THESE TWO, AND NOT THE THREE WT-666 ASKED FOR. The ticket named `document`,
    /// `meeting_summary` and `email_thread` — those are KNOWLEDGE CHUNK source types, a different
    /// field in a different store (the Qdrant payload the Knowledge page filters on, written by
    /// the embedding worker). A document's chunks are published as `document` however the document
    /// itself arrived. Applying the ticket's list here would have rejected every upload the web
    /// makes (`upload`) and orphaned the External-member meeting exception in
    /// <see cref="SourceTypeMeeting"/>, which the access evaluator matches on.
    /// </remarks>
    public static readonly string[] SupportedSourceTypes =
    [
        SourceTypeUpload,
        SourceTypeMeeting
    ];

    /// <summary>
    /// Matches workspace.workspace_documents.name — VARCHAR(255).
    /// </summary>
    /// <remarks>
    /// Nothing checked it. A 300-character name reached Postgres verbatim, raised 22001, and the
    /// catch-all in UploadDocumentAsync turned it into a 500 "unexpected error" — after the
    /// encrypted blob had already been written to storage.
    /// </remarks>
    public const int MaxDocumentNameLength = 255;

    /// <summary>
    /// How much a reviewer may write when rejecting, and an uploader when answering. WT-633.
    /// </summary>
    /// <remarks>
    /// Not a column width — this text lives inside the audit row's Metadata jsonb, which has no
    /// length of its own. It is a cap on how much free text one API call can push into an
    /// append-only table, set generously enough that nobody writing a real explanation meets it.
    /// </remarks>
    public const int MaxRejectionReasonLength = 2000;

    /// <summary>
    /// What to do when the bytes being uploaded are already in this workspace.
    /// </summary>
    /// <remarks>
    /// The default is Reject: a 409 that names the existing document, so the web can ASK. Silently
    /// creating a second copy is what WT-666 reported — two document rows and two sets of AI
    /// chunks for one file, with nothing to tell them apart.
    /// </remarks>
    public static class DuplicateStrategies
    {
        public const string Reject = "reject";
        public const string Skip = "skip";
        public const string Replace = "replace";
        public const string CreateNew = "create_new";

        public static readonly string[] All = [Reject, Skip, Replace, CreateNew];
    }

    public static readonly string[] SupportedUploadExtensions =
    [
        ".pdf",
        ".docx",
        ".xlsx",
        ".md",
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".gif"
    ];

    public static readonly string[] AiReadableExtensions =
    [
        ".pdf",
        ".docx",
        ".xlsx",
        ".md"
    ];

    public static readonly string[] ImageExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".gif"
    ];

    public static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".md"] = "text/markdown",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif"
        };

    public static class StorageEncryption
    {
        public const int IvSize = 16;
        public const int SignatureSize = 64;
        public const string DefaultS3BucketName = "warptalk-workspace-documents";
    }

    public static class AuditActions
    {
        public const string UploadDocument = "UploadDocument";
        public const string GetDocumentDetails = "GetDocumentDetails";
        public const string PatchDocumentMetadata = "PatchDocumentMetadata";
        public const string UpdateExtractedText = "UpdateExtractedText";
        public const string AddAccessPolicy = "AddAccessPolicy";
        public const string RemoveAccessPolicy = "RemoveAccessPolicy";
        public const string ApproveDocument = "ApproveDocument";
        public const string RejectDocument = "RejectDocument";

        /// <summary>
        /// A rejected document's uploader replaced the file in place. WT-633.
        /// </summary>
        /// <remarks>
        /// Spelled as the ticket names it. Its Metadata carries `previousStorageKey`, the previous
        /// file name and the reviewer's rejection comment, which is the whole point: the audit row
        /// is the only record that the superseded blob ever existed, and the only place the
        /// reviewer's words survive the document going back to pending.
        /// </remarks>
        public const string ReuploadDocument = "Reuploaded";
        public const string DownloadDocument = "DownloadDocument";
        public const string DeleteDocument = "DeleteDocument";
        public const string ArchiveDocument = "ArchiveDocument";
        public const string RestoreDocument = "RestoreDocument";
        public const string SecurityScanCompleted = "SecurityScanCompleted";
        public const string EmbeddingIndexed = "EmbeddingIndexed";
        public const string EmbeddingFailed = "EmbeddingFailed";
        public const string EmbeddingBlocked = "EmbeddingBlocked";
    }

    public static class LifecycleEvents
    {
        public const string Created = "DocumentCreated";
        public const string PendingApproval = "DocumentPendingApproval";
        public const string Updated = "DocumentUpdated";
        public const string Approved = "DocumentApproved";
        public const string Rejected = "DocumentRejected";
        public const string Processing = "DocumentProcessing";
        public const string Completed = "DocumentCompleted";
        public const string Failed = "DocumentFailed";
        public const string Archived = "DocumentArchived";
        public const string Restored = "DocumentRestored";
        public const string Deleted = "DocumentDeleted";
    }
}

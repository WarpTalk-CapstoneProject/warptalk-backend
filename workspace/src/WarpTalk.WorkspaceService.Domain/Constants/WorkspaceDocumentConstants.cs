namespace WarpTalk.WorkspaceService.Domain.Constants;

public static class WorkspaceDocumentConstants
{
    public const string SensitiveConfidentialityLevel = "restricted";
    public const string NonSensitiveConfidentialityLevel = "public_internal";
    public const string RetentionStateActive = "active";
    public const string SourceTypeMeeting = "meeting";
    public const string LocalStorageProvider = "local";

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
        public const string AddAccessPolicy = "AddAccessPolicy";
        public const string RemoveAccessPolicy = "RemoveAccessPolicy";
        public const string ApproveDocument = "ApproveDocument";
        public const string RejectDocument = "RejectDocument";
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

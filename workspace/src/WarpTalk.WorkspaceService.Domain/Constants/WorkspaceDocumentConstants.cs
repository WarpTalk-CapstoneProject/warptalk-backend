namespace WarpTalk.WorkspaceService.Domain.Constants;

public static class WorkspaceDocumentConstants
{
    public const string SensitiveConfidentialityLevel = "restricted";
    public const string NonSensitiveConfidentialityLevel = "public_internal";
    public const string RetentionStateActive = "active";
    public const string SourceTypeMeeting = "meeting";
    public const string LocalStorageProvider = "local";
    public const string DownloadUrlFormat = "/api/v1/workspaces/{0}/documents/{1}/download";
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
    }
}

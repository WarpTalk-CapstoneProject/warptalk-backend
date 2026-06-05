namespace WarpTalk.WorkspaceService.Domain.Constants;

public static class WorkspaceDocumentConstants
{
    public const string SensitiveConfidentialityLevel = "restricted";
    public const string NonSensitiveConfidentialityLevel = "public_internal";
    public const string RetentionStateActive = "active";
    public const string SourceTypeMeeting = "meeting";
    public const string LocalStorageProvider = "local";
    public const string DownloadUrlFormat = "/api/v1/workspaces/{0}/documents/{1}/download";
}

namespace WarpTalk.AuthService.Domain.Constants;

public static class WorkspaceConstants
{
    // Workspace Plan Tiers
    public const string PlanTierFree = "free";
    public const string PlanTierBusiness = "business";

    // Workspace Settings Defaults
    public const string DefaultWorkspaceLanguage = "en";
    public const string DefaultWorkspaceTimezone = "UTC";
    public const int DefaultWorkspaceMaxActiveRooms = 5;
    public const int DefaultWorkspaceArtifactRetentionDays = 30;
}

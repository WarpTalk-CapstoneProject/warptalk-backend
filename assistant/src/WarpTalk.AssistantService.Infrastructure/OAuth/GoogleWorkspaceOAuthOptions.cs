namespace WarpTalk.AssistantService.Infrastructure.OAuth;

public class GoogleWorkspaceOAuthOptions
{
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// One redirect URI for the whole provider, not one per plugin.
    /// </summary>
    /// <remarks>
    /// google_drive, google_calendar and google_meet share this Google OAuth client, so a
    /// per-plugin path would need three entries in Google Cloud Console and a console change for
    /// every Google product added. The plugin key travels in the protected <c>state</c> instead.
    /// The old <c>google_workspace/oauth/callback</c> route still resolves, so an environment whose
    /// console entry has not been updated yet keeps working.
    /// </remarks>
    public string RedirectUri { get; set; } = "http://localhost:5200/api/v1/assistant/plugins/oauth/google/callback";

    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    public string RevokeEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";

    public string UserInfoEndpoint { get; set; } = "https://openidconnect.googleapis.com/v1/userinfo";
}

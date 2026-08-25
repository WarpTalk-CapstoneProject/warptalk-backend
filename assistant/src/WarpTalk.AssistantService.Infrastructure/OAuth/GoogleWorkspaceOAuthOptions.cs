namespace WarpTalk.AssistantService.Infrastructure.OAuth;

public class GoogleWorkspaceOAuthOptions
{
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string RedirectUri { get; set; } = "http://localhost:5108/api/v1/assistant/plugins/google_workspace/oauth/callback";

    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    public string UserInfoEndpoint { get; set; } = "https://openidconnect.googleapis.com/v1/userinfo";
}

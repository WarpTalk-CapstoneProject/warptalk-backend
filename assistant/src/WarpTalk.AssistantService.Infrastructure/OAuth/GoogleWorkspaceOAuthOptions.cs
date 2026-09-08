namespace WarpTalk.AssistantService.Infrastructure.OAuth;

public class GoogleWorkspaceOAuthOptions
{
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// One redirect URI for the whole provider, not one per plugin. Every new connect is
    /// authorized against this one.
    /// </summary>
    /// <remarks>
    /// google_drive, google_calendar and google_meet share this Google OAuth client, so a
    /// per-plugin path would need three entries in Google Cloud Console and a console change for
    /// every Google product added. The plugin key travels in the protected <c>state</c> instead.
    /// <para>
    /// OPERATOR: this exact string must be registered as an authorized redirect URI on the Google
    /// OAuth client, because it is sent on both legs of the flow - the authorization request and
    /// the token exchange - and Google matches it exactly on both. A deployment whose console entry
    /// still names the old per-plugin path cannot connect any Google plugin; there is no fallback
    /// that would make it work, and <see cref="LegacyRedirectUri"/> is not one - that one only
    /// finishes flows that were already authorized against the old path.
    /// </para>
    /// </remarks>
    public string RedirectUri { get; set; } = "http://localhost:5200/api/v1/assistant/plugins/oauth/google/callback";

    /// <summary>
    /// The retired per-plugin redirect URI, used only to finish a consent that was authorized
    /// against it before <see cref="RedirectUri"/> shipped.
    /// </summary>
    /// <remarks>
    /// Google matches <c>redirect_uri</c> on the token exchange against the value the authorization
    /// request carried, so a consent started before the WT-646 deploy can only be exchanged with
    /// the URI it started with. The callback that receives it is the legacy
    /// <c>{pluginKey}/oauth/callback</c> route, and the plugin key it can name is
    /// <c>google_workspace</c> - the row the split retired - because every flow started since the
    /// deploy was authorized against the provider-scoped URI and comes back to it.
    /// <para>
    /// OPERATOR: while this is set, BOTH it and <see cref="RedirectUri"/> have to stay registered
    /// as authorized redirect URIs on the Google OAuth client. An authorization code lives minutes,
    /// so nothing authorized against the old URI can still be in flight once a deploy has settled:
    /// once it has, delete this setting, the legacy route, and the console entry together. Left
    /// empty, a callback arriving on the legacy route fails cleanly rather than exchanging with the
    /// wrong URI.
    /// </para>
    /// </remarks>
    public string LegacyRedirectUri { get; set; } = "http://localhost:5200/api/v1/assistant/plugins/google_workspace/oauth/callback";

    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    public string RevokeEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";

    public string UserInfoEndpoint { get; set; } = "https://openidconnect.googleapis.com/v1/userinfo";
}

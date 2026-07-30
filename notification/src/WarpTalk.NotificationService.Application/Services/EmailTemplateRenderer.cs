namespace WarpTalk.NotificationService.Application.Services;

public static class EmailTemplateRenderer
{
    private const string BaseHtmlWrapperStart = """
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <style>
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; color: #0f172a; margin: 0; padding: 0; }
            .container { max-width: 560px; margin: 40px auto; background: #ffffff; border: 1px solid #e2e8f0; border-radius: 16px; padding: 36px; box-shadow: 0 4px 20px rgba(0,0,0,0.03); }
            .logo { font-size: 22px; font-weight: 700; color: #4f46e5; text-decoration: none; display: inline-block; margin-bottom: 24px; tracking: -0.02em; }
            h1 { font-size: 20px; font-weight: 600; color: #0f172a; margin-top: 0; margin-bottom: 12px; letter-spacing: -0.01em; }
            p { font-size: 14px; line-height: 1.6; color: #475569; margin: 0 0 16px 0; }
            .card { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 12px; padding: 20px; margin: 20px 0; }
            .card p { color: #334155; }
            .btn { display: inline-block; background: #4f46e5; color: #ffffff !important; font-size: 14px; font-weight: 600; padding: 12px 24px; border-radius: 9999px; text-decoration: none; margin-top: 12px; }
            .btn:hover { background: #4338ca; }
            .badge { display: inline-block; background: #e0e7ff; color: #4338ca; font-size: 12px; font-weight: 600; padding: 4px 10px; border-radius: 6px; }
            .footer { margin-top: 36px; padding-top: 20px; border-top: 1px solid #f1f5f9; font-size: 12px; color: #94a3b8; text-align: center; }
          </style>
        </head>
        <body>
          <div class="container">
            <a href="https://warptalk.app" class="logo">WarpTalk</a>
        """;

    private const string BaseHtmlWrapperEnd = """
            <div class="footer">
              <p style="font-size:12px; color:#94a3b8;">© 2026 WarpTalk — Real-time Multilingual Translation & Collaboration.</p>
            </div>
          </div>
        </body>
        </html>
        """;

    public static string RenderGenericNotification(string title, string content, string? actionUrl)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeContent = System.Net.WebUtility.HtmlEncode(content)
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);
        var safeActionUrl = Uri.TryCreate(actionUrl, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? System.Net.WebUtility.HtmlEncode(uri.ToString())
            : "https://warptalk.app";

        return $"""
            {BaseHtmlWrapperStart}
            <h1>{safeTitle}</h1>
            <div class="card"><p>{safeContent}</p></div>
            <a href="{safeActionUrl}" class="btn">Open WarpTalk</a>
            {BaseHtmlWrapperEnd}
            """;
    }

    public static string RenderWorkspaceInvite(string inviterName, string workspaceName, string roleName, string inviteUrl)
    {
        return $"""
            {BaseHtmlWrapperStart}
            <h1>Workspace Invitation</h1>
            <p>You have been invited to join a workspace on WarpTalk.</p>
            <div class="card">
              <p style="color:#0f172a; font-weight:600; margin-bottom:8px;">{inviterName} invited you to join <strong>{workspaceName}</strong></p>
              <p style="margin-bottom:0;">Role: <span class="badge">{roleName}</span></p>
            </div>
            <a href="{inviteUrl}" class="btn">Accept Invitation & Join Workspace</a>
            <p style="margin-top:20px; font-size:12px; color:#94a3b8;">This invitation link will expire in 7 days.</p>
            {BaseHtmlWrapperEnd}
            """;
    }

    public static string RenderMeetingInvite(string hostName, string meetingTitle, string scheduledTime, string roomCode, string joinUrl, string sourceLang, string targetLangs)
    {
        return $"""
            {BaseHtmlWrapperStart}
            <h1>Meeting Invitation</h1>
            <p>You have been invited to a live translation room.</p>
            <div class="card">
              <p style="color:#0f172a; font-weight:600; font-size:16px; margin-bottom:12px;">{meetingTitle}</p>
              <p><strong>Host:</strong> {hostName}</p>
              <p><strong>Scheduled:</strong> {scheduledTime}</p>
              <p><strong>Languages:</strong> {sourceLang} ➔ {targetLangs}</p>
              <p style="margin-bottom:0;"><strong>Meeting Code:</strong> <span class="badge" style="font-family:monospace;">{roomCode}</span></p>
            </div>
            <a href="{joinUrl}" class="btn">Join Translation Room</a>
            {BaseHtmlWrapperEnd}
            """;
    }

    public static string RenderMeetingReminder(string meetingTitle, string scheduledTime, string joinUrl)
    {
        return $"""
            {BaseHtmlWrapperStart}
            <h1>⏰ Starting Soon in 15 Minutes</h1>
            <p>Your scheduled meeting is about to begin.</p>
            <div class="card">
              <p style="color:#0f172a; font-weight:600; font-size:16px; margin-bottom:8px;">{meetingTitle}</p>
              <p style="margin-bottom:0;">Starts at {scheduledTime}</p>
            </div>
            <a href="{joinUrl}" class="btn">Enter Meeting Room Now</a>
            {BaseHtmlWrapperEnd}
            """;
    }

    public static string RenderAISummaryReady(string meetingTitle, string summarySnippet, string viewUrl)
    {
        return $"""
            {BaseHtmlWrapperStart}
            <h1>📄 AI Meeting Summary Ready</h1>
            <p>The AI pipeline has processed your meeting transcript and generated key decisions.</p>
            <div class="card">
              <p style="color:#0f172a; font-weight:600; font-size:16px; margin-bottom:8px;">{meetingTitle}</p>
              <p style="margin-bottom:0; font-style:italic; color:#475569;">"{summarySnippet}"</p>
            </div>
            <a href="{viewUrl}" class="btn">View Full Summary & Transcript</a>
            {BaseHtmlWrapperEnd}
            """;
    }

    public static string RenderBillingCreditLow(int remainingCredits, int remainingMinutes, string topUpUrl)
    {
        return $"""
            {BaseHtmlWrapperStart}
            <h1>⚠️ Low Credit Balance Warning</h1>
            <p>Your WarpTalk AI voice and translation credits are running low.</p>
            <div class="card" style="border-color:#fde68a; background:#fffbeb;">
              <p style="color:#92400e; font-weight:600; margin-bottom:8px;">Remaining Balance: <span style="color:#b45309;">{remainingCredits} credits (~{remainingMinutes} mins)</span></p>
              <p style="margin-bottom:0; color:#b45309;">Active translation rooms will automatically pause when your balance reaches 0.</p>
            </div>
            <a href="{topUpUrl}" class="btn" style="background:#d97706;">Top Up Credits Now</a>
            {BaseHtmlWrapperEnd}
            """;
    }
}

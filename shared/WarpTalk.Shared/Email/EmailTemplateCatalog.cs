namespace WarpTalk.Shared.Email;

/// <summary>
/// Every transactional email the platform sends, with the wording each one uses when no admin has
/// edited it.
///
/// The list is derived from the senders, not designed: each entry names the class that sends it,
/// and each class asks <see cref="IEmailTemplateComposer"/> for exactly this key. Adding an email
/// here without a sender is a template nobody reads; adding a sender without an entry throws at
/// the first send (<see cref="Get"/>), which is the point — the two cannot drift silently.
///
/// The defaults reproduce what each email said before the templates became editable, moved into
/// the one layout the auth and workspace emails already shared. The meeting emails used to carry
/// their own indigo layout and the notification copy a third one; all three now wear the brand.
/// </summary>
public static class EmailTemplateCatalog
{
    public const string ProviderResend = "Resend";
    public const string ProviderSmtp = "SMTP";

    public const string AuthVerifyEmail = "auth.verify-email";
    public const string AuthPasswordReset = "auth.password-reset";
    public const string AuthStaffInvitation = "auth.staff-invitation";
    public const string WorkspaceInvitation = "workspace.invitation";
    public const string WorkspaceJoinRequestApproved = "workspace.join-request-approved";
    public const string MeetingInvitation = "meeting.invitation";
    public const string MeetingReminder = "meeting.reminder";
    public const string NotificationEmailCopy = "notification.email-copy";

    private static readonly EmailTemplateDefinition[] Definitions =
    [
        new(
            AuthVerifyEmail,
            "Email verification",
            "Confirms a new account's email address.",
            "Auth service",
            ProviderResend,
            "Registration, a verification resend, and Google sign-in on an account whose email is not yet verified.",
            IsLive: true,
            DormantReason: null,
            [
                new("FullName", "The account holder's full name.", "Linh Nguyen"),
                new("VerifyUrl", "The one-time verification link.", "https://app.warptalk.vn/verify-email?token=sample-token", Required: true),
            ],
            new EmailTemplateContent(
                "Verify your WarpTalk email",
                "Verify your email address",
                Paragraph("Hi <strong style=\"color: #18181B;\">{{FullName}}</strong>,", bottom: 16) +
                Paragraph("Welcome to WarpTalk! Please verify your email address by clicking the button below to complete your registration.", bottom: 28) +
                Button("{{VerifyUrl}}", "Verify Email Address &rarr;") +
                LinkBox("{{VerifyUrl}}") +
                Disclaimer("This email was sent to you by WarpTalk. If you did not initiate this request, please contact support."))),

        new(
            AuthPasswordReset,
            "Password reset",
            "Lets someone who forgot their password choose a new one.",
            "Auth service",
            ProviderResend,
            "Forgot password.",
            IsLive: true,
            DormantReason: null,
            [
                new("FullName", "The account holder's full name.", "Linh Nguyen"),
                new("ResetUrl", "The one-time password reset link.", "https://app.warptalk.vn/reset-password?token=sample-token", Required: true),
            ],
            new EmailTemplateContent(
                "Reset your WarpTalk password",
                "Reset your password",
                Paragraph("Hi <strong style=\"color: #18181B;\">{{FullName}}</strong>,", bottom: 16) +
                Paragraph("We received a request to reset your WarpTalk account password. Click the button below to choose a new password.", bottom: 28) +
                Button("{{ResetUrl}}", "Reset Password &rarr;") +
                LinkBox("{{ResetUrl}}") +
                "<p style=\"margin: 0; font-size: 13px; color: #71717A;\">This link expires soon and can only be used once.</p>" +
                Disclaimer("This email was sent to you by WarpTalk. If you did not initiate this request, please contact support."))),

        new(
            AuthStaffInvitation,
            "Staff access",
            "Tells someone they have been given access to the WarpTalk admin portal.",
            "Auth service",
            ProviderResend,
            "Someone with staff.manage adds a person on /admin/staff.",
            IsLive: true,
            DormantReason: null,
            [
                new("InviterName", "Who gave the access.", "Minh Tran"),
                new("RoleName", "The staff role granted.", "Support"),
                new("SignInUrl", "Where to sign in. The access activates on the first sign-in with this verified address.", "https://app.warptalk.vn/login?redirect=%2Fadmin", Required: true),
                new("ExpiresIn", "How long an invitation to a new address stays open.", "7 days"),
            ],
            new EmailTemplateContent(
                "You've been given access to the WarpTalk admin portal",
                "You've been given staff access",
                Paragraph("Hello,", bottom: 16) +
                Paragraph("<strong style=\"color: #18181B; font-weight: 600;\">{{InviterName}}</strong> has given you access to the WarpTalk admin portal as " + Badge("{{RoleName}}") + ".", bottom: 20) +
                "<p style=\"margin: 0 0 28px 0; font-size: 14px; line-height: 1.6; color: #71717A;\">Sign in with this email address. If you do not have a WarpTalk account yet, create one with this address and verify it: the access activates the first time you sign in, within {{ExpiresIn}}.</p>" +
                Button("{{SignInUrl}}", "Open the admin portal &rarr;") +
                LinkBox("{{SignInUrl}}") +
                Disclaimer("If you were not expecting this, you can ignore this email. Nothing changes until someone signs in with this address."))),

        new(
            WorkspaceInvitation,
            "Workspace invitation",
            "Invites someone to join a workspace.",
            "Workspace service",
            ProviderResend,
            "A workspace admin invites a member, or retries delivery of a pending invitation.",
            IsLive: true,
            DormantReason: null,
            [
                new("WorkspaceName", "The workspace's name.", "Acme Localization"),
                new("InviterName", "Who sent the invitation.", "Minh Tran"),
                new("RoleName", "The role the invitee will have.", "Member"),
                new("JoinUrl", "The invitation link.", "https://app.warptalk.vn/login?token=sample-token", Required: true),
                new("AppBaseUrl", "The web app's address.", "https://app.warptalk.vn"),
            ],
            new EmailTemplateContent(
                "You've been invited to join {{WorkspaceName}} on WarpTalk",
                "You've been invited to join {{WorkspaceName}}",
                Paragraph("Hello,", bottom: 16) +
                Paragraph("<strong style=\"color: #18181B; font-weight: 600;\">{{InviterName}}</strong> has invited you to join the <strong style=\"color: #18181B; font-weight: 600;\">{{WorkspaceName}}</strong> workspace as a " + Badge("{{RoleName}}") + ".", bottom: 20) +
                "<p style=\"margin: 0 0 28px 0; font-size: 14px; line-height: 1.6; color: #71717A;\">WarpTalk is an AI-powered real-time translation and workspace collaboration platform built for high-performing teams.</p>" +
                Button("{{JoinUrl}}", "Accept &amp; Join Workspace &rarr;") +
                LinkBox("{{JoinUrl}}") +
                Disclaimer("This invitation link was sent to you by WarpTalk. If you were not expecting this invitation, you can safely ignore this email."))),

        new(
            WorkspaceJoinRequestApproved,
            "Join request approved",
            "Tells someone their request to join a workspace was approved.",
            "Workspace service",
            ProviderResend,
            "A workspace admin approves a request to join.",
            IsLive: true,
            DormantReason: null,
            [
                new("WorkspaceName", "The workspace's name.", "Acme Localization"),
                new("MembershipType", "internal or external.", "internal"),
                new("JoinUrl", "A link straight into the workspace.", "https://app.warptalk.vn/acme/home", Required: true),
                new("AppBaseUrl", "The web app's address.", "https://app.warptalk.vn"),
            ],
            new EmailTemplateContent(
                "Your request to join {{WorkspaceName}} was approved",
                "Your request was approved",
                Paragraph("Your request to join <strong style=\"color: #18181B; font-weight: 600;\">{{WorkspaceName}}</strong> was approved as a " + Badge("Member") + " with <strong style=\"color: #18181B; font-weight: 600;\">{{MembershipType}}</strong> membership.", bottom: 20) +
                Button("{{JoinUrl}}", "Open Workspace &rarr;") +
                LinkBox("{{JoinUrl}}") +
                Disclaimer("This approval email was sent by WarpTalk. If you did not submit this join request, contact the workspace administrators."))),

        new(
            MeetingInvitation,
            "Meeting invitation",
            "Invites someone to a scheduled meeting.",
            "Translation room service",
            ProviderSmtp,
            "Creating a meeting with invitees (only the first occurrence of a recurring series), inviting participants, and adding invitees in meeting settings.",
            IsLive: true,
            DormantReason: null,
            [
                new("ParticipantName", "The invitee's name, or \"Participant\" when unknown.", "Participant"),
                new("MeetingTitle", "The meeting's title, as the host typed it.", "Weekly localization sync"),
                new("ScheduledTime", "When the meeting starts.", "2026-09-30 10:00 UTC"),
                new("MeetingLink", "The link that opens the meeting.", "https://app.warptalk.vn/room/sample", Required: true),
            ],
            new EmailTemplateContent(
                "Invitation to Meeting: {{MeetingTitle}}",
                "WarpTalk Meeting Invitation",
                Paragraph("Hello <strong style=\"color: #18181B;\">{{ParticipantName}}</strong>,", bottom: 16) +
                Paragraph("You have been invited to a meeting:", bottom: 12) +
                DetailCard(("Title", "{{MeetingTitle}}"), ("Scheduled Time", "{{ScheduledTime}}")) +
                Button("{{MeetingLink}}", "Join Meeting &rarr;") +
                LinkBox("{{MeetingLink}}"))),

        new(
            MeetingReminder,
            "Meeting reminder",
            "Reminds an invitee that a meeting is about to start.",
            "Translation room service",
            ProviderSmtp,
            "None today.",
            IsLive: false,
            DormantReason: "Implemented, but nothing calls it. Meeting reminders go out as in-app notifications only.",
            [
                new("ParticipantName", "The invitee's name.", "Linh Nguyen"),
                new("MeetingTitle", "The meeting's title, as the host typed it.", "Weekly localization sync"),
                new("StartsIn", "How long until it starts.", "15 minutes"),
                new("MeetingLink", "The link that opens the meeting.", "https://app.warptalk.vn/room/sample", Required: true),
            ],
            new EmailTemplateContent(
                "Reminder: Meeting '{{MeetingTitle}}' starts in {{StartsIn}}",
                "Meeting Reminder",
                Paragraph("Hello <strong style=\"color: #18181B;\">{{ParticipantName}}</strong>,", bottom: 16) +
                Paragraph("This is a reminder that your meeting is starting soon:", bottom: 12) +
                DetailCard(("Title", "{{MeetingTitle}}"), ("Starts In", "{{StartsIn}}")) +
                Button("{{MeetingLink}}", "Join Meeting Now &rarr;"))),

        new(
            NotificationEmailCopy,
            "Notification email copy",
            "An email copy of an in-app notification.",
            "Notification service",
            ProviderResend,
            "Any notification, for a recipient with email notifications on.",
            IsLive: false,
            DormantReason: "Sent only when a notification's metadata carries a toEmail or email key, and no producer sets either.",
            [
                new("Title", "The notification's title.", "Your meeting summary is ready"),
                new("Content", "The notification's text.", "The summary for \"Weekly localization sync\" is ready to read.", Multiline: true),
                new("ActionUrl", "Where the notification points.", "https://app.warptalk.vn"),
            ],
            new EmailTemplateContent(
                "{{Title}}",
                "{{Title}}",
                "<div style=\"background-color: #FAFAFA; border: 1px solid #F4F4F5; border-radius: 10px; padding: 16px; margin: 0 0 24px 0;\">" +
                "<p style=\"margin: 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">{{Content}}</p></div>" +
                Button("{{ActionUrl}}", "Open WarpTalk &rarr;"))),
    ];

    // The inbox preview line under each subject. Added with the v2 CMS; editable per locale.
    private static readonly Dictionary<string, string> DefaultPreheaders = new(StringComparer.Ordinal)
    {
        [AuthVerifyEmail] = "One click to confirm your address and finish signing up.",
        [AuthPasswordReset] = "Use this link to choose a new password. It expires soon.",
        [WorkspaceInvitation] = "{{InviterName}} invited you to collaborate on WarpTalk.",
        [WorkspaceJoinRequestApproved] = "You can open {{WorkspaceName}} now.",
        [MeetingInvitation] = "{{MeetingTitle}} · {{ScheduledTime}}",
        [MeetingReminder] = "{{MeetingTitle}} starts in {{StartsIn}}.",
        [NotificationEmailCopy] = "",
    };

    private static readonly EmailTemplateDefinition[] WithPreheaders = Definitions
        .Select(definition => definition with
        {
            Default = definition.Default with { Preheader = DefaultPreheaders.GetValueOrDefault(definition.Key, string.Empty) },
        })
        .ToArray();

    private static readonly Dictionary<string, EmailTemplateDefinition> ByKey =
        WithPreheaders.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    public static IReadOnlyList<EmailTemplateDefinition> All => WithPreheaders;

    public static EmailTemplateDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>A key a sender uses. Throws for an unknown one: that is a code defect, not data.</summary>
    public static EmailTemplateDefinition Get(string key) =>
        Find(key) ?? throw new KeyNotFoundException($"No email template is registered under '{key}'.");

    /// <summary>The values the preview and the test email use.</summary>
    public static IReadOnlyDictionary<string, string> SampleValues(EmailTemplateDefinition definition) =>
        definition.Variables.ToDictionary(variable => variable.Name, variable => variable.Sample, StringComparer.Ordinal);

    // ── Building blocks of the default wording ───────────────────────────────────────────────

    private static string Paragraph(string html, int bottom) =>
        $"<p style=\"margin: 0 0 {bottom}px 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">{html}</p>";

    private static string Badge(string text) =>
        $"<span style=\"background-color: #F4F4F5; border: 1px solid #E4E4E7; color: #18181B; font-size: 13px; font-weight: 600; padding: 2px 8px; border-radius: 6px;\">{text}</span>";

    private static string Button(string href, string label) =>
        $"<div style=\"margin: 32px 0;\"><a href=\"{href}\" target=\"_blank\" style=\"display: inline-block; background-color: #18181B; color: #FFFFFF; font-size: 14px; font-weight: 600; text-decoration: none; padding: 14px 28px; border-radius: 10px; box-shadow: 0 2px 6px rgba(0, 0, 0, 0.08);\">{label}</a></div>";

    private static string LinkBox(string href) =>
        "<div style=\"background-color: #FAFAFA; border: 1px solid #F4F4F5; border-radius: 10px; padding: 16px; margin: 24px 0;\">" +
        "<p style=\"margin: 0 0 6px 0; font-size: 12px; font-weight: 500; color: #71717A;\">Or copy and paste this link into your browser:</p>" +
        $"<a href=\"{href}\" target=\"_blank\" style=\"font-size: 13px; color: #D97757; text-decoration: none; word-break: break-all;\">{href}</a></div>";

    private static string DetailCard(params (string Label, string Value)[] rows) =>
        "<div style=\"background-color: #FAFAFA; border: 1px solid #F4F4F5; border-radius: 10px; padding: 16px; margin: 0 0 8px 0;\">" +
        string.Concat(rows.Select(row =>
            $"<p style=\"margin: 0 0 6px 0; font-size: 14px; line-height: 1.6; color: #3F3F46;\"><strong style=\"color: #18181B;\">{row.Label}:</strong> {row.Value}</p>")) +
        "</div>";

    private static string Disclaimer(string text) =>
        "<hr style=\"border: 0; border-top: 1px solid #F4F4F5; margin: 32px 0 24px 0;\" />" +
        $"<p style=\"margin: 0; font-size: 12px; line-height: 1.6; color: #A1A1AA;\">{text}</p>";
}

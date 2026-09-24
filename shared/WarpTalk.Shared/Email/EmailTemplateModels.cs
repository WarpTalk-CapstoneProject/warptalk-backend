namespace WarpTalk.Shared.Email;

/// <summary>
/// One value a template may reference as <c>{{Name}}</c>.
/// </summary>
/// <param name="Name">The placeholder name, exactly as written between the braces.</param>
/// <param name="Description">What the sender puts there, for the admin editing the template.</param>
/// <param name="Sample">What the preview and the test email use in its place.</param>
/// <param name="Required">
/// The template must reference it somewhere. Set on the one value an email exists to deliver — the
/// verification link, the join link — because a template saved without it sends an email that
/// cannot be acted on, and nothing downstream would notice.
/// </param>
/// <param name="Multiline">Line breaks in the value become <c>&lt;br /&gt;</c> in the HTML body.</param>
public sealed record EmailTemplateVariable(
    string Name,
    string Description,
    string Sample,
    bool Required = false,
    bool Multiline = false);

/// <summary>
/// The three editable parts of an email. The surrounding layout (logo, card, footer) is not one
/// of them: it is the brand, and it is shared by every email the platform sends.
/// </summary>
/// <param name="Subject">Plain text. Values are substituted as-is; a mail header is not HTML.</param>
/// <param name="Heading">Plain text shown as the email's title. HTML-encoded when rendered.</param>
/// <param name="BodyHtml">An HTML fragment. Substituted values are HTML-encoded.</param>
public sealed record EmailTemplateContent(string Subject, string Heading, string BodyHtml);

/// <summary>
/// A transactional email the platform sends, and the built-in wording it falls back to.
/// </summary>
/// <param name="Provider">How the sender delivers it: "Resend" (auth, workspace, notification) or "SMTP" (translation-room).</param>
public sealed record EmailTemplateDefinition(
    string Key,
    string Name,
    string Description,
    string Service,
    string Provider,
    string Trigger,
    bool IsLive,
    string? DormantReason,
    IReadOnlyList<EmailTemplateVariable> Variables,
    EmailTemplateContent Default);

/// <summary>An email ready to hand to a provider.</summary>
public sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody);

/// <summary>
/// A template an admin saved, as the sender reads it. <see cref="Version"/> increases on every
/// save, restore and reset.
/// </summary>
public sealed record StoredEmailTemplate(string Subject, string Heading, string BodyHtml, int Version);

/// <summary>A problem with a template, tied to the field it is in.</summary>
/// <param name="Field">subject, heading or bodyHtml.</param>
/// <param name="Code">A stable machine code, e.g. UNKNOWN_VARIABLE.</param>
public sealed record EmailTemplateIssue(string Field, string Code, string Message);

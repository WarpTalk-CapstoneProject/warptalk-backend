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
/// The editable content of one email. The surrounding layout (logo, card, footer) is not part of
/// it: that is an <see cref="EmailLayout"/>, shared by every email that uses it.
/// </summary>
/// <param name="Subject">Plain text. Values are substituted as-is; a mail header is not HTML.</param>
/// <param name="Heading">Plain text shown as the email's title. HTML-encoded when rendered.</param>
/// <param name="BodyHtml">An HTML fragment. Substituted values are HTML-encoded.</param>
/// <param name="Preheader">The inbox preview line shown after the subject. Plain text.</param>
/// <param name="TextBody">
/// The plain-text part, written by hand. Null or blank derives it from the HTML, so the two parts
/// cannot say different things unless someone deliberately makes them.
/// </param>
public sealed record EmailTemplateContent(
    string Subject,
    string Heading,
    string BodyHtml,
    string Preheader = "",
    string? TextBody = null);

/// <summary>
/// The wrapper an email's content is rendered into: brand header, footer, card, dark-mode rules.
/// Slots: <c>{{content}}</c> (required, once), <c>{{heading}}</c>, <c>{{preheader}}</c>,
/// <c>{{subject}}</c>, and the section <c>{{#heading}}…{{/heading}}</c>, rendered only when the
/// email has a heading. Layouts carry no template variables: they are shared by every email.
/// </summary>
/// <param name="TextTemplate">The plain-text wrapper, with <c>{{content}}</c>. Null uses the text as-is.</param>
/// <param name="DarkCss">
/// CSS applied when the reader's mail client is in dark mode (inside
/// <c>@media (prefers-color-scheme: dark)</c>), and unconditionally in a dark preview.
/// </param>
public sealed record EmailLayout(string Html, string? TextTemplate = null, string? DarkCss = null);

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
public sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody)
{
    /// <summary>The catalog key it was composed from, for delivery counting.</summary>
    public string? TemplateKey { get; init; }

    /// <summary>The locale whose content was used (after fallback).</summary>
    public string Locale { get; init; } = EmailLocales.Default;

    /// <summary>The published version used; 0 for the built-in wording.</summary>
    public int Version { get; init; }
}

/// <summary>
/// The published version of an email, as a sender reads it: content with every partial already
/// expanded, and the layout it uses. <see cref="Version"/> increases on every publish.
/// </summary>
public sealed record StoredEmailTemplate(string Subject, string Heading, string BodyHtml, int Version)
{
    public string Preheader { get; init; } = string.Empty;

    /// <summary>Hand-written plain text, or null to derive it from the HTML.</summary>
    public string? TextBody { get; init; }

    /// <summary>The layout's HTML with partials expanded, or null for the built-in layout.</summary>
    public string? LayoutHtml { get; init; }

    public string? LayoutText { get; init; }

    public string? LayoutDarkCss { get; init; }

    /// <summary>The locale this content is written in (the fallback may differ from the one asked for).</summary>
    public string Locale { get; init; } = EmailLocales.Default;
}

/// <summary>How a render is presented: the send path never forces dark mode; a preview may.</summary>
public sealed record EmailRenderOptions(bool ForceDark = false);

/// <summary>The locales email content can be written in, and how a requested one falls back.</summary>
public static class EmailLocales
{
    public const string Default = "en";

    public static readonly string[] Supported = ["en", "vi", "ja"];

    /// <summary>"vi-VN" → "vi"; anything unsupported or blank → null.</summary>
    public static string? Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var primary = locale.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return Supported.Contains(primary) ? primary : null;
    }

    /// <summary>The locales to try, in order: the requested one, then the default.</summary>
    public static IReadOnlyList<string> FallbackChain(string? locale)
    {
        var normalized = Normalize(locale);
        return normalized is null || normalized == Default ? [Default] : [normalized, Default];
    }
}

/// <summary>A problem with a template, tied to the field it is in.</summary>
/// <param name="Field">subject, heading or bodyHtml.</param>
/// <param name="Code">A stable machine code, e.g. UNKNOWN_VARIABLE.</param>
public sealed record EmailTemplateIssue(string Field, string Code, string Message);

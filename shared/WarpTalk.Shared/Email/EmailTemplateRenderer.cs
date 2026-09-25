using System.Net;
using System.Text.RegularExpressions;

namespace WarpTalk.Shared.Email;

/// <summary>
/// Turns email content, its layout and its values into an email, and says what is wrong with
/// content, a layout or a partial before anyone publishes it.
///
/// One renderer for the senders, the admin preview and the test email, so what an admin previews
/// is byte-for-byte what a recipient receives.
///
/// Encoding rules, per field:
///   subject    plain text; values go in as-is (a mail header is not HTML), with line breaks
///              folded to spaces so a value cannot start a second header.
///   preheader  plain text; HTML-encoded into the layout's hidden preview span.
///   heading    plain text; the whole heading is HTML-encoded after substitution.
///   body       admin-authored HTML; every substituted value is HTML-encoded, so a meeting title
///              typed by a host can never put markup in someone else's inbox.
///   text       hand-written plain text; values go in as-is.
///
/// Partials (<c>{{&gt; key}}</c>) are expanded by the notification service, which owns them, before
/// a template reaches this class — see <see cref="ExpandPartials"/>.
/// </summary>
public static partial class EmailTemplateRenderer
{
    public const int MaxSubjectLength = 255;
    public const int MaxHeadingLength = 255;
    public const int MaxPreheaderLength = 255;
    public const int MaxBodyLength = 50_000;
    public const int MaxTextLength = 50_000;

    /// <summary>The layout slots. Lower-case on purpose: template variables are PascalCase.</summary>
    public const string SlotContent = "content";
    public const string SlotHeading = "heading";
    public const string SlotPreheader = "preheader";
    public const string SlotSubject = "subject";

    private static readonly string[] LayoutSlots = [SlotContent, SlotHeading, SlotPreheader, SlotSubject];

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    // Braces that look like an attempt at a placeholder but are not one: "{{ }}", "{{Full Name}}".
    // Partial ("{{> x}}") and section ("{{#x}}", "{{/x}}") tags are their own syntax, not typos.
    [GeneratedRegex(@"\{\{(?![>#/])(?!\s*[A-Za-z][A-Za-z0-9_]*\s*\}\})[^{}]*\}\}")]
    private static partial Regex MalformedPlaceholderPattern();

    [GeneratedRegex(@"\{\{>\s*([a-z0-9][a-z0-9_-]*)\s*\}\}")]
    private static partial Regex PartialPattern();

    [GeneratedRegex(@"\{\{#heading\}\}(.*?)\{\{/heading\}\}", RegexOptions.Singleline)]
    private static partial Regex HeadingSectionPattern();

    [GeneratedRegex(@"\{\{[#/][^{}]*\}\}")]
    private static partial Regex AnySectionTagPattern();

    [GeneratedRegex(@"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|link|meta|base|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTagPattern();

    // A layout owns <head>, so it may carry <style> and <meta>; nothing that runs or submits.
    [GeneratedRegex(@"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|link|base)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenLayoutTagPattern();

    [GeneratedRegex(@"<[^>]*\s(on[a-z]+)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerPattern();

    [GeneratedRegex(@"\b(?:href|src|action|background)\s*=\s*[""']?\s*(javascript|vbscript|data)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrlPattern();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakPattern();

    [GeneratedRegex(@"</\s*(p|div|h[1-6]|li|tr|table|ul|ol|blockquote)\s*>|<hr\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"<(style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonTextElementPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpacePattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesPattern();

    [GeneratedRegex(@"</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadClosePattern();

    /// <summary>The built-in brand layout. Used whenever no layout has been published.</summary>
    public static EmailLayout BuiltInLayout { get; } = new(BuiltInLayoutHtml, TextTemplate: null, DarkCss: BuiltInDarkCss);

    /// <summary>Every placeholder name a piece of text references, in order of first use.</summary>
    public static IReadOnlyList<string> ReferencedVariables(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return PlaceholderPattern().Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every partial key a piece of text includes, in order of first use.</summary>
    public static IReadOnlyList<string> ReferencedPartials(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return PartialPattern().Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Replaces every <c>{{&gt; key}}</c> with that partial's HTML. One level only: a partial that
    /// includes another is refused on save, so there is no recursion to bound. Unknown keys are
    /// left in place and reported, so validation can name them.
    /// </summary>
    public static string ExpandPartials(
        string? text,
        IReadOnlyDictionary<string, string> partials,
        ICollection<string>? unknown = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return PartialPattern().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            if (partials.TryGetValue(key, out var html)) return html;
            unknown?.Add(key);
            return match.Value;
        });
    }

    /// <summary>
    /// Everything that would make this content unsafe or unusable. Empty means it may be published.
    /// Expects partials already expanded.
    /// </summary>
    public static IReadOnlyList<EmailTemplateIssue> Validate(EmailTemplateDefinition definition, EmailTemplateContent content)
    {
        var issues = new List<EmailTemplateIssue>();
        var known = definition.Variables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

        var subject = content.Subject ?? string.Empty;
        var heading = content.Heading ?? string.Empty;
        var body = content.BodyHtml ?? string.Empty;
        var preheader = content.Preheader ?? string.Empty;
        var text = content.TextBody ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject))
            issues.Add(new("subject", "REQUIRED", "The subject is required."));
        else if (subject.Length > MaxSubjectLength)
            issues.Add(new("subject", "TOO_LONG", $"The subject must be {MaxSubjectLength} characters or fewer."));
        if (subject.Contains('\r') || subject.Contains('\n'))
            issues.Add(new("subject", "LINE_BREAK", "The subject must be a single line."));

        if (heading.Length > MaxHeadingLength)
            issues.Add(new("heading", "TOO_LONG", $"The heading must be {MaxHeadingLength} characters or fewer."));

        if (preheader.Length > MaxPreheaderLength)
            issues.Add(new("preheader", "TOO_LONG", $"The preheader must be {MaxPreheaderLength} characters or fewer."));
        if (preheader.Contains('\r') || preheader.Contains('\n'))
            issues.Add(new("preheader", "LINE_BREAK", "The preheader must be a single line."));

        if (string.IsNullOrWhiteSpace(body))
            issues.Add(new("bodyHtml", "REQUIRED", "The body is required."));
        else if (body.Length > MaxBodyLength)
            issues.Add(new("bodyHtml", "TOO_LONG", $"The body must be {MaxBodyLength:N0} characters or fewer."));

        if (text.Length > MaxTextLength)
            issues.Add(new("textBody", "TOO_LONG", $"The plain-text version must be {MaxTextLength:N0} characters or fewer."));

        foreach (var (field, value) in new[]
                 {
                     ("subject", subject), ("preheader", preheader), ("heading", heading), ("bodyHtml", body), ("textBody", text),
                 })
        {
            foreach (var name in ReferencedVariables(value).Where(name => !known.Contains(name)))
            {
                issues.Add(new(field, "UNKNOWN_VARIABLE",
                    $"{{{{{name}}}}} is not a variable this email has. Available: {string.Join(", ", known.Select(k => $"{{{{{k}}}}}"))}."));
            }

            foreach (Match malformed in MalformedPlaceholderPattern().Matches(value))
            {
                issues.Add(new(field, "MALFORMED_VARIABLE",
                    $"{malformed.Value} is not a valid variable. Write it as {{{{Name}}}} with no spaces inside the name."));
            }

            foreach (var partial in ReferencedPartials(value))
            {
                issues.Add(new(field, "UNKNOWN_PARTIAL", $"{{{{> {partial}}}}} is not a published block."));
            }
        }

        var referenced = ReferencedVariables(subject)
            .Concat(ReferencedVariables(heading))
            .Concat(ReferencedVariables(body))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var required in definition.Variables.Where(variable => variable.Required && !referenced.Contains(variable.Name)))
        {
            issues.Add(new("bodyHtml", "MISSING_REQUIRED_VARIABLE",
                $"{{{{{required.Name}}}}} must appear in the email: {required.Description}"));
        }

        // The hand-written text part is the only thing a plain-text reader sees, so it has to
        // carry the link too.
        if (!string.IsNullOrWhiteSpace(text))
        {
            var textReferenced = ReferencedVariables(text).ToHashSet(StringComparer.Ordinal);
            foreach (var required in definition.Variables.Where(variable => variable.Required && !textReferenced.Contains(variable.Name)))
            {
                issues.Add(new("textBody", "MISSING_REQUIRED_VARIABLE",
                    $"{{{{{required.Name}}}}} must appear in the plain-text version too: {required.Description}"));
            }
        }

        issues.AddRange(UnsafeHtml("bodyHtml", body, ForbiddenTagPattern()));
        return issues;
    }

    /// <summary>What is wrong with a layout. Expects partials already expanded.</summary>
    public static IReadOnlyList<EmailTemplateIssue> ValidateLayout(EmailLayout layout)
    {
        var issues = new List<EmailTemplateIssue>();
        var html = layout.Html ?? string.Empty;

        if (string.IsNullOrWhiteSpace(html))
            issues.Add(new("html", "REQUIRED", "The layout HTML is required."));
        else if (html.Length > MaxBodyLength)
            issues.Add(new("html", "TOO_LONG", $"The layout must be {MaxBodyLength:N0} characters or fewer."));

        var contentSlots = PlaceholderPattern().Matches(html).Count(match => match.Groups[1].Value == SlotContent);
        if (contentSlots != 1)
            issues.Add(new("html", "CONTENT_SLOT", "The layout must contain {{content}} exactly once — it is where each email's body goes."));

        foreach (var (field, value) in new[] { ("html", html), ("textTemplate", layout.TextTemplate ?? string.Empty) })
        {
            foreach (var name in ReferencedVariables(value).Where(name => !LayoutSlots.Contains(name)))
            {
                issues.Add(new(field, "UNKNOWN_SLOT",
                    $"{{{{{name}}}}} is not a layout slot. A layout is shared by every email, so it can use only {{{{content}}}}, {{{{heading}}}}, {{{{preheader}}}} and {{{{subject}}}}."));
            }
            foreach (Match malformed in MalformedPlaceholderPattern().Matches(value))
                issues.Add(new(field, "MALFORMED_VARIABLE", $"{malformed.Value} is not a valid slot."));
            foreach (var partial in ReferencedPartials(value))
                issues.Add(new(field, "UNKNOWN_PARTIAL", $"{{{{> {partial}}}}} is not a published block."));
        }

        var sections = AnySectionTagPattern().Matches(HeadingSectionPattern().Replace(html, "$1"));
        if (sections.Count > 0)
            issues.Add(new("html", "UNKNOWN_SECTION", $"{sections[0].Value} is not supported. The only section is {{{{#heading}}}}…{{{{/heading}}}}."));

        if (!string.IsNullOrWhiteSpace(layout.TextTemplate) && !layout.TextTemplate.Contains("{{content}}", StringComparison.Ordinal))
            issues.Add(new("textTemplate", "CONTENT_SLOT", "The plain-text wrapper must contain {{content}}."));

        if (layout.DarkCss is { } css && css.Contains('<'))
            issues.Add(new("darkCss", "UNSAFE_CSS", "Dark-mode CSS cannot contain markup."));
        if (layout.DarkCss is { Length: > MaxTextLength })
            issues.Add(new("darkCss", "TOO_LONG", "The dark-mode CSS is too long."));

        issues.AddRange(UnsafeHtml("html", html, ForbiddenLayoutTagPattern()));
        return issues;
    }

    /// <summary>What is wrong with a reusable block. Variables are checked in each email that uses it.</summary>
    public static IReadOnlyList<EmailTemplateIssue> ValidatePartial(string? html)
    {
        var issues = new List<EmailTemplateIssue>();
        var value = html ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new("html", "REQUIRED", "The block HTML is required."));
        else if (value.Length > MaxBodyLength)
            issues.Add(new("html", "TOO_LONG", $"A block must be {MaxBodyLength:N0} characters or fewer."));
        if (ReferencedPartials(value).Count > 0)
            issues.Add(new("html", "NESTED_PARTIAL", "A block cannot include another block."));
        if (ReferencedVariables(value).Any(LayoutSlots.Contains))
            issues.Add(new("html", "LAYOUT_SLOT", "Layout slots ({{content}} and the like) belong in a layout, not a block."));
        foreach (Match malformed in MalformedPlaceholderPattern().Matches(value))
            issues.Add(new("html", "MALFORMED_VARIABLE", $"{malformed.Value} is not a valid variable."));
        issues.AddRange(UnsafeHtml("html", value, ForbiddenTagPattern()));
        return issues;
    }

    private static IEnumerable<EmailTemplateIssue> UnsafeHtml(string field, string html, Regex forbiddenTags)
    {
        var tag = forbiddenTags.Match(html);
        if (tag.Success)
            yield return new(field, "UNSAFE_HTML", $"<{tag.Groups[1].Value.ToLowerInvariant()}> is not allowed here.");
        var handler = EventHandlerPattern().Match(html);
        if (handler.Success)
            yield return new(field, "UNSAFE_HTML", $"Event handler attributes ({handler.Groups[1].Value.ToLowerInvariant()}=) are not allowed.");
        var scriptUrl = ScriptUrlPattern().Match(html);
        if (scriptUrl.Success)
            yield return new(field, "UNSAFE_HTML", $"{scriptUrl.Groups[1].Value.ToLowerInvariant()}: links are not allowed.");
    }

    /// <summary>
    /// The finished email. Placeholders with no value are left visible rather than blanked, so a
    /// preview shows exactly which one is unfilled.
    /// </summary>
    public static RenderedEmail Render(
        EmailTemplateDefinition definition,
        EmailTemplateContent content,
        IReadOnlyDictionary<string, string> values,
        EmailLayout? layout = null,
        EmailRenderOptions? options = null)
    {
        layout ??= BuiltInLayout;
        options ??= new EmailRenderOptions();

        var multiline = definition.Variables
            .Where(variable => variable.Multiline)
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.Ordinal);

        var subject = Substitute(content.Subject, values, FoldLines).Trim();
        var preheader = Substitute(content.Preheader ?? string.Empty, values, FoldLines).Trim();
        var headingText = Substitute(content.Heading ?? string.Empty, values, value => value).Trim();
        var body = PlaceholderPattern().Replace(content.BodyHtml ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out var value)) return match.Value;
            var encoded = WebUtility.HtmlEncode(value ?? string.Empty);
            return multiline.Contains(name)
                ? encoded.Replace("\r\n", "<br />").Replace("\n", "<br />")
                : encoded;
        });

        var html = ApplyLayout(layout, body, WebUtility.HtmlEncode(headingText), WebUtility.HtmlEncode(preheader), WebUtility.HtmlEncode(subject), options);

        var text = string.IsNullOrWhiteSpace(content.TextBody)
            ? ToPlainText((string.IsNullOrWhiteSpace(headingText) ? string.Empty : $"<h1>{WebUtility.HtmlEncode(headingText)}</h1>") + body)
            : Substitute(content.TextBody, values, value => value).Trim();
        if (!string.IsNullOrWhiteSpace(layout.TextTemplate))
        {
            text = layout.TextTemplate
                .Replace("{{content}}", text, StringComparison.Ordinal)
                .Replace("{{subject}}", subject, StringComparison.Ordinal)
                .Replace("{{preheader}}", preheader, StringComparison.Ordinal)
                .Replace("{{heading}}", headingText, StringComparison.Ordinal)
                .Trim();
        }

        return new RenderedEmail(subject, html, text) { TemplateKey = definition.Key };
    }

    /// <summary>A one-line plain-text field (subject, preheader) with its values filled in.</summary>
    public static string RenderPlainLine(string? template, IReadOnlyDictionary<string, string> values) =>
        Substitute(template ?? string.Empty, values, FoldLines).Trim();

    private static string FoldLines(string value) => value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static string Substitute(string template, IReadOnlyDictionary<string, string> values, Func<string, string> transform) =>
        PlaceholderPattern().Replace(template ?? string.Empty, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? transform(value ?? string.Empty) : match.Value);

    private static string ApplyLayout(
        EmailLayout layout,
        string bodyHtml,
        string encodedHeading,
        string encodedPreheader,
        string encodedSubject,
        EmailRenderOptions options)
    {
        var html = HeadingSectionPattern().Replace(layout.Html, match =>
            string.IsNullOrWhiteSpace(encodedHeading) ? string.Empty : match.Groups[1].Value);

        // Content last, so a body that happens to contain "{{heading}}" as text is not a slot.
        html = PlaceholderPattern().Replace(html, match => match.Groups[1].Value switch
        {
            SlotHeading => encodedHeading,
            SlotPreheader => encodedPreheader,
            SlotSubject => encodedSubject,
            _ => match.Value,
        });
        var contentIndex = html.IndexOf("{{content}}", StringComparison.Ordinal);
        if (contentIndex >= 0)
            html = html[..contentIndex] + bodyHtml + html[(contentIndex + "{{content}}".Length)..];

        if (!string.IsNullOrWhiteSpace(layout.DarkCss))
        {
            var css = layout.DarkCss.Trim();
            var head = options.ForceDark
                ? $"<meta name=\"color-scheme\" content=\"dark\"><style>{css}</style>"
                : $"<meta name=\"color-scheme\" content=\"light dark\"><meta name=\"supported-color-schemes\" content=\"light dark\"><style>@media (prefers-color-scheme: dark) {{ {css} }}</style>";
            var close = HeadClosePattern().Match(html);
            html = close.Success ? html.Insert(close.Index, head) : head + html;
        }

        return html;
    }

    /// <summary>
    /// The plain-text part, derived from HTML when nobody wrote one by hand.
    /// </summary>
    public static string ToPlainText(string html)
    {
        var text = NonTextElementPattern().Replace(html, string.Empty);
        text = AnchorPattern().Replace(text, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            var label = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups[3].Value, string.Empty)).Trim();
            if (string.IsNullOrEmpty(label) || string.Equals(label, href, StringComparison.Ordinal)) return href;
            return $"{label} ({href})";
        });
        text = LineBreakPattern().Replace(text, "\n");
        text = ListItemPattern().Replace(text, "\n- ");
        text = BlockEndPattern().Replace(text, "\n\n");
        text = TagPattern().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => HorizontalSpacePattern().Replace(line, " ").Trim());
        var joined = string.Join('\n', lines);
        return BlankLinesPattern().Replace(joined, "\n\n").Trim();
    }

    private const string BuiltInDarkCss =
        ".wt-page { background-color: #111113 !important; } " +
        ".wt-card { background-color: #18181B !important; border-color: #27272A !important; } " +
        ".wt-card h1, .wt-card p, .wt-card strong, .wt-card span, .wt-card div, .wt-brand { color: #F4F4F5 !important; } " +
        ".wt-footer { color: #71717A !important; }";

    private const string BuiltInLayoutHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>{{subject}}</title>
        </head>
        <body class="wt-page" style="margin: 0; padding: 0; width: 100%; background-color: #FBF9F5; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
        <span style="display: none !important; visibility: hidden; opacity: 0; color: transparent; height: 0; width: 0; overflow: hidden; mso-hide: all;">{{preheader}}</span>
        <table role="presentation" class="wt-page" width="100%" border="0" cellspacing="0" cellpadding="0" style="background-color: #FBF9F5; padding: 48px 16px;"><tr><td align="center">
        <table role="presentation" class="wt-card" width="100%" border="0" cellspacing="0" cellpadding="0" style="max-width: 540px; background-color: #FFFFFF; border: 1px solid #E4E4E7; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 20px rgba(0, 0, 0, 0.03);">
        <tr><td style="padding: 40px 36px 0 36px;"><div class="wt-brand" style="font-size: 22px; font-weight: 700; color: #18181B; letter-spacing: -0.5px;">WarpTalk<span style="color: #D97757; font-weight: 900;">.</span></div></td></tr>
        <tr><td style="padding: 24px 36px 40px 36px;">
        {{#heading}}<h1 style="margin: 0 0 20px 0; font-size: 22px; font-weight: 700; color: #18181B; line-height: 1.3; letter-spacing: -0.3px;">{{heading}}</h1>{{/heading}}
        {{content}}
        </td></tr>
        </table>
        <table role="presentation" width="100%" border="0" cellspacing="0" cellpadding="0" style="max-width: 540px; margin-top: 24px;"><tr><td class="wt-footer" align="center" style="font-size: 12px; color: #A1A1AA;">&copy; 2026 WarpTalk Inc. &bull; Real-time AI Workspace Collaboration</td></tr></table>
        </td></tr></table>
        </body>
        </html>
        """;
}

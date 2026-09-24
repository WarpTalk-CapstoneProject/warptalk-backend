using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WarpTalk.Shared.Email;

/// <summary>
/// Turns a template and its values into an email, and says what is wrong with a template before
/// anyone saves it.
///
/// One renderer for the sender, the admin preview and the test email, so what an admin previews
/// is byte-for-byte what a recipient receives.
///
/// Encoding rules, per field:
///   subject  plain text; values go in as-is (a mail header is not HTML), with line breaks folded
///            to spaces so a value cannot start a second header.
///   heading  plain text; the whole heading is HTML-encoded after substitution.
///   body     admin-authored HTML; every substituted value is HTML-encoded, so a meeting title
///            typed by a host can never put markup in someone else's inbox.
/// </summary>
public static partial class EmailTemplateRenderer
{
    public const int MaxSubjectLength = 255;
    public const int MaxHeadingLength = 255;
    public const int MaxBodyLength = 50_000;

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    // Braces that look like an attempt at a placeholder but are not one: "{{ }}", "{{Full Name}}".
    [GeneratedRegex(@"\{\{(?!\s*[A-Za-z][A-Za-z0-9_]*\s*\}\})[^{}]*\}\}")]
    private static partial Regex MalformedPlaceholderPattern();

    [GeneratedRegex(@"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|link|meta|base|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTagPattern();

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

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpacePattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesPattern();

    /// <summary>Every placeholder name a piece of text references, in order of first use.</summary>
    public static IReadOnlyList<string> ReferencedVariables(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return PlaceholderPattern().Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Everything that would make this template unsafe or unusable. Empty means it may be saved.
    /// </summary>
    public static IReadOnlyList<EmailTemplateIssue> Validate(EmailTemplateDefinition definition, EmailTemplateContent content)
    {
        var issues = new List<EmailTemplateIssue>();
        var known = definition.Variables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);

        var subject = content.Subject ?? string.Empty;
        var heading = content.Heading ?? string.Empty;
        var body = content.BodyHtml ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject))
            issues.Add(new("subject", "REQUIRED", "The subject is required."));
        else if (subject.Length > MaxSubjectLength)
            issues.Add(new("subject", "TOO_LONG", $"The subject must be {MaxSubjectLength} characters or fewer."));
        if (subject.Contains('\r') || subject.Contains('\n'))
            issues.Add(new("subject", "LINE_BREAK", "The subject must be a single line."));

        if (heading.Length > MaxHeadingLength)
            issues.Add(new("heading", "TOO_LONG", $"The heading must be {MaxHeadingLength} characters or fewer."));

        if (string.IsNullOrWhiteSpace(body))
            issues.Add(new("bodyHtml", "REQUIRED", "The body is required."));
        else if (body.Length > MaxBodyLength)
            issues.Add(new("bodyHtml", "TOO_LONG", $"The body must be {MaxBodyLength:N0} characters or fewer."));

        foreach (var (field, text) in new[] { ("subject", subject), ("heading", heading), ("bodyHtml", body) })
        {
            foreach (var name in ReferencedVariables(text).Where(name => !known.Contains(name)))
            {
                issues.Add(new(field, "UNKNOWN_VARIABLE",
                    $"{{{{{name}}}}} is not a variable this email has. Available: {string.Join(", ", known.Select(k => $"{{{{{k}}}}}"))}."));
            }

            foreach (Match malformed in MalformedPlaceholderPattern().Matches(text))
            {
                issues.Add(new(field, "MALFORMED_VARIABLE",
                    $"{malformed.Value} is not a valid variable. Write it as {{{{Name}}}} with no spaces inside the name."));
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

        var tag = ForbiddenTagPattern().Match(body);
        if (tag.Success)
            issues.Add(new("bodyHtml", "UNSAFE_HTML", $"<{tag.Groups[1].Value.ToLowerInvariant()}> is not allowed in an email body."));
        var handler = EventHandlerPattern().Match(body);
        if (handler.Success)
            issues.Add(new("bodyHtml", "UNSAFE_HTML", $"Event handler attributes ({handler.Groups[1].Value.ToLowerInvariant()}=) are not allowed."));
        var scriptUrl = ScriptUrlPattern().Match(body);
        if (scriptUrl.Success)
            issues.Add(new("bodyHtml", "UNSAFE_HTML", $"{scriptUrl.Groups[1].Value.ToLowerInvariant()}: links are not allowed."));

        return issues;
    }

    /// <summary>
    /// The finished email. Placeholders with no value are left visible rather than blanked, so a
    /// preview shows exactly which one is unfilled.
    /// </summary>
    public static RenderedEmail Render(
        EmailTemplateDefinition definition,
        EmailTemplateContent content,
        IReadOnlyDictionary<string, string> values)
    {
        var multiline = definition.Variables
            .Where(variable => variable.Multiline)
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.Ordinal);

        var subject = Substitute(content.Subject, values, value => value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '));
        var heading = WebUtility.HtmlEncode(Substitute(content.Heading ?? string.Empty, values, value => value));
        var body = PlaceholderPattern().Replace(content.BodyHtml ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out var value)) return match.Value;
            var encoded = WebUtility.HtmlEncode(value ?? string.Empty);
            return multiline.Contains(name)
                ? encoded.Replace("\r\n", "<br />").Replace("\n", "<br />")
                : encoded;
        });

        var html = Layout(heading, body);
        var text = ToPlainText((string.IsNullOrWhiteSpace(heading) ? string.Empty : $"<h1>{heading}</h1>") + body);
        return new RenderedEmail(subject.Trim(), html, text);
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, string> values, Func<string, string> transform) =>
        PlaceholderPattern().Replace(template ?? string.Empty, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? transform(value ?? string.Empty) : match.Value);

    /// <summary>
    /// The plain-text part. Derived from the HTML rather than edited separately, so the two parts
    /// of one email cannot say different things.
    /// </summary>
    public static string ToPlainText(string html)
    {
        var text = AnchorPattern().Replace(html, match =>
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

    /// <summary>The brand layout every email shares.</summary>
    private static string Layout(string encodedHeading, string bodyHtml)
    {
        var headingBlock = string.IsNullOrWhiteSpace(encodedHeading)
            ? string.Empty
            : $"<h1 style=\"margin: 0 0 20px 0; font-size: 22px; font-weight: 700; color: #18181B; line-height: 1.3; letter-spacing: -0.3px;\">{encodedHeading}</h1>";

        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        builder.Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        builder.Append("<title>").Append(encodedHeading).Append("</title>\n</head>\n");
        builder.Append("<body style=\"margin: 0; padding: 0; width: 100%; background-color: #FBF9F5; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;\">\n");
        builder.Append("<table role=\"presentation\" width=\"100%\" border=\"0\" cellspacing=\"0\" cellpadding=\"0\" style=\"background-color: #FBF9F5; padding: 48px 16px;\"><tr><td align=\"center\">\n");
        builder.Append("<table role=\"presentation\" width=\"100%\" border=\"0\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width: 540px; background-color: #FFFFFF; border: 1px solid #E4E4E7; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 20px rgba(0, 0, 0, 0.03);\">\n");
        builder.Append("<tr><td style=\"padding: 40px 36px 0 36px;\"><div style=\"font-size: 22px; font-weight: 700; color: #18181B; letter-spacing: -0.5px;\">WarpTalk<span style=\"color: #D97757; font-weight: 900;\">.</span></div></td></tr>\n");
        builder.Append("<tr><td style=\"padding: 24px 36px 40px 36px;\">\n");
        builder.Append(headingBlock).Append('\n');
        builder.Append(bodyHtml).Append('\n');
        builder.Append("</td></tr>\n</table>\n");
        builder.Append("<table role=\"presentation\" width=\"100%\" border=\"0\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width: 540px; margin-top: 24px;\"><tr><td align=\"center\" style=\"font-size: 12px; color: #A1A1AA;\">&copy; 2026 WarpTalk Inc. &bull; Real-time AI Workspace Collaboration</td></tr></table>\n");
        builder.Append("</td></tr></table>\n</body>\n</html>");
        return builder.ToString();
    }
}

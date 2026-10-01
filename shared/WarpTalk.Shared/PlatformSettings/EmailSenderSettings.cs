using System.Text.RegularExpressions;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>Who transactional e-mail comes from, and where replies go.</summary>
public sealed record EmailSender(string Name, string Address, string? ReplyTo)
{
    /// <summary>The RFC 5322 "Name &lt;address&gt;" form every provider accepts.</summary>
    public string From => $"{Name} <{Address}>";
}

/// <summary>
/// The sender every service's e-mail path uses: notifications.email.from_name / from_address /
/// reply_to from /admin/settings, each falling back to that service's own deploy-time
/// configuration. Read per message, so a change applies to the next e-mail sent.
/// </summary>
public static class EmailSenderSettings
{
    private static readonly Regex NameAddr = new(@"^\s*(?<name>[^<]*?)\s*<(?<address>[^>]+)>\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static async ValueTask<EmailSender> ResolveAsync(
        IPlatformSettings? settings, string fallbackName, string fallbackAddress, CancellationToken ct = default)
    {
        if (settings is null) return new EmailSender(fallbackName, fallbackAddress, null);
        var name = await settings.GetStringAsync(PlatformSettingsCatalog.EmailFromName, fallbackName, ct: ct);
        var address = await settings.GetStringAsync(PlatformSettingsCatalog.EmailFromAddress, fallbackAddress, ct: ct);
        var replyTo = await settings.GetStringAsync(PlatformSettingsCatalog.EmailReplyTo, string.Empty, ct: ct);
        return new EmailSender(
            string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim(),
            string.IsNullOrWhiteSpace(address) ? fallbackAddress : address.Trim(),
            string.IsNullOrWhiteSpace(replyTo) ? null : replyTo.Trim());
    }

    /// <summary>Splits a configured "Name &lt;address&gt;" (or a bare address) into its parts.</summary>
    public static (string Name, string Address) Parse(string configured, string defaultName = "WarpTalk")
    {
        var match = NameAddr.Match(configured ?? string.Empty);
        if (match.Success)
        {
            var name = match.Groups["name"].Value.Trim().Trim('"');
            return (name.Length == 0 ? defaultName : name, match.Groups["address"].Value.Trim());
        }

        return (defaultName, (configured ?? string.Empty).Trim());
    }
}

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>
/// The From line an inbox shows: the notification service's own sender identity
/// (Resend:FromEmail, "Name &lt;address&gt;" or a bare address). Only for previews — the providers
/// read their own configuration when they send.
/// </summary>
public sealed record EmailEnvelope(string FromName, string FromAddress)
{
    public static readonly EmailEnvelope Default = new("WarpTalk", "no-reply@warptalk.vn");

    public static EmailEnvelope Parse(string? configured, string? fallbackName = null)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Default;
        var value = configured.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var name = value[..open].Trim().Trim('"');
            var address = value[(open + 1)..close].Trim();
            return new EmailEnvelope(string.IsNullOrEmpty(name) ? fallbackName ?? Default.FromName : name, address);
        }
        return new EmailEnvelope(fallbackName ?? Default.FromName, value);
    }
}

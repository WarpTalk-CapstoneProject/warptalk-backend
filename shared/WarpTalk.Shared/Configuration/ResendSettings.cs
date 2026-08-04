namespace WarpTalk.Shared.Configuration;

public class ResendSettings
{
    public const string SectionName = "Resend";
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "no-reply@warptalk.io.vn";
    public string FromName { get; set; } = "WarpTalk";
}

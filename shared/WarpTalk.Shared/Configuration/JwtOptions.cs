namespace WarpTalk.Shared.Configuration;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "WarpTalk.AuthService";
    public string Audience { get; set; } = "WarpTalk";
}

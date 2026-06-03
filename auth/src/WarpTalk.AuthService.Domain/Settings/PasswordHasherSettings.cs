namespace WarpTalk.AuthService.Domain.Settings;

public class PasswordHasherSettings
{
    public int SaltSize { get; set; } = 16;
    public int HashSize { get; set; } = 32;
    public int Iterations { get; set; } = 100_000;
    public string Algorithm { get; set; } = "SHA512";
    public string VersionPrefix { get; set; } = "v2";
}

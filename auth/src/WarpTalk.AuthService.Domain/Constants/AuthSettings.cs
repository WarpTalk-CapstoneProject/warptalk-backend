namespace WarpTalk.AuthService.Domain.Constants;

public class AuthSettings
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 15;
    public string DefaultRole { get; set; } = "member";
}

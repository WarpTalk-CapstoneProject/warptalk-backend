namespace WarpTalk.AuthService.Domain.Settings;

using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;

public class AuthSettings
{
    public int MaxFailedAttempts { get; set; } = AuthConstants.DefaultMaxFailedAttempts;
    public int LockoutDurationMinutes { get; set; } = AuthConstants.DefaultLockoutDurationMinutes;
    public string DefaultRole { get; set; } = AuthConstants.DefaultRole;
}

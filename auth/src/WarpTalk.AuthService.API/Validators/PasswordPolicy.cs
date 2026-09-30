using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.AuthService.API.Validators;

/// <summary>
/// The password floor every password-setting validator applies: security.password.min_length from
/// /admin/settings, falling back to <see cref="UserConstants.PasswordMinLength"/>. Never below that
/// constant and never above the maximum length, whatever is stored.
/// </summary>
public static class PasswordPolicy
{
    public static int MinLength(IPlatformSettings? settings)
    {
        var value = settings?.GetInt32(PlatformSettingsCatalog.PasswordMinLength, UserConstants.PasswordMinLength)
                    ?? UserConstants.PasswordMinLength;
        return Math.Clamp(value, UserConstants.PasswordMinLength, UserConstants.PasswordMaxLength);
    }

    public static string PasswordMessage(int minLength)
        => minLength == UserConstants.PasswordMinLength
            ? ApiMessageConstants.ValidationMessages.PasswordMinLength
            : $"Password must be at least {minLength} characters long.";

    public static string NewPasswordMessage(int minLength)
        => minLength == UserConstants.PasswordMinLength
            ? ApiMessageConstants.ValidationMessages.NewPasswordMinLength
            : $"New password must be at least {minLength} characters long.";
}

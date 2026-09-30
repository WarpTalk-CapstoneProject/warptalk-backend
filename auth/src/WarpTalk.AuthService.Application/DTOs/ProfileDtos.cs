using System;
using System.Collections.Generic;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Application.DTOs;

public record UpdateProfileRequest(string? FullName, string? Phone, string? PreferredLanguage, string? Timezone);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record UserDto(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    string? Phone,
    string? PreferredLanguage,
    string? Timezone,
    bool EmailVerified,
    AccountStatus Status,
    IReadOnlyList<string> Roles,
    // Sign-in methods, so Settings > Connected accounts can show link state and explain why
    // Unlink is refused (UnlinkGoogleAsync requires a local password). Booleans only — the
    // Google subject id and the password hash never leave the service.
    bool GoogleLinked = false,
    bool HasPassword = false
);

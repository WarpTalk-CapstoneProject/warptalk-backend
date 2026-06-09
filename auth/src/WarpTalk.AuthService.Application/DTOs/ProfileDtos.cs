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
    IReadOnlyList<string> Roles
);

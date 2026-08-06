using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record UserIdentityDto(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    string? PreferredLanguage
);

public record UserLanguageDefaultsDto(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage
);

public record RoleDto(
    Guid Id,
    string Name,
    string? Description
);

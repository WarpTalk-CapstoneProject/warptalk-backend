using System;
using System.Collections.Generic;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class UserMapper
{
    public static UserDto ToDto(User user, List<string> roles)
    {
        return new UserDto(
            user.Id,
            user.Email,
            user.FullName,
            user.AvatarUrl,
            user.Phone,
            user.PreferredLanguage,
            user.Timezone,
            user.EmailVerified,
            UserStatusHelper.GetAccountStatus(user),
            roles.AsReadOnly()
        );
    }

    public static UserDto ToDto(User user, string defaultRole)
    {
        var roles = user.GetRoles(defaultRole);
        return ToDto(user, roles);
    }

    public static User ToUser(RegisterRequest request, string passwordHash)
    {
        return new User
        {
            Email = request.Email.ToLowerInvariant().Trim(),
            PasswordHash = passwordHash,
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static User ToUser(GoogleAuthPayload payload)
    {
        return new User
        {
            Email = payload.Email.ToLowerInvariant().Trim(),
            PasswordHash = "",
            FullName = payload.Name ?? "Google User",
            AvatarUrl = payload.Picture,
            EmailVerified = payload.EmailVerified,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            GoogleId = payload.Subject
        };
    }
}

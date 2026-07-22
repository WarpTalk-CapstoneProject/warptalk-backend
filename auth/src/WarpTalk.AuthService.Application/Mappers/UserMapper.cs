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
            // Client-generated, matching RegisterInvitedAsync's pattern — the id column
            // defaults to uuidv7() DB-side, but RegisterAsync needs the real id BEFORE
            // insert to build the linked UserSetting row in the same SaveChanges batch.
            Id = Guid.NewGuid(),
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
            // See ToUser(RegisterRequest, string) — GoogleLoginAsync creates a linked
            // UserSetting in the same SaveChanges batch and needs the real id upfront.
            Id = Guid.NewGuid(),
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

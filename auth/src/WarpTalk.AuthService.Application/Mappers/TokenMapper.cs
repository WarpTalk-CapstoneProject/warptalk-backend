using System;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Mappers;

public static class TokenMapper
{
    public static RefreshToken ToRefreshToken(Guid userId, string rawToken, int expiryDays, string? ip, string? device)
    {
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = TokenHasher.Hash(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(expiryDays),
            IpAddress = ip,
            DeviceInfo = device,
            CreatedAt = DateTime.UtcNow
        };
    }
}

using System;
using System.Collections.Generic;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Helpers;

public static class TokenHelper
{
    public static (string AccessToken, string RefreshToken, DateTime ExpiresAt) GenerateTokens(
        this IJwtTokenGenerator jwtGenerator, 
        User user, 
        List<string> roles)
    {
        var accessToken = jwtGenerator.GenerateAccessToken(user.Id, user.Email, user.EmailVerified, roles);
        var refreshToken = jwtGenerator.GenerateRefreshToken();
        var expiresAt = DateTime.UtcNow.AddMinutes(jwtGenerator.AccessTokenExpiryMinutes);
        return (accessToken, refreshToken, expiresAt);
    }

    public static RefreshToken CreateRefreshTokenEntity(
        this IJwtTokenGenerator jwtGenerator,
        Guid userId,
        string rawToken,
        string? ip,
        string? device,
        Guid? familyId = null)
    {
        return TokenMapper.ToRefreshToken(userId, rawToken, jwtGenerator.RefreshTokenExpiryDays, ip, device, familyId);
    }
}

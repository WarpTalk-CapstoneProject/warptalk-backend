using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Application.Helpers;

public static class AuthResponseHelper
{
    public static async Task<AuthResponse> CreateAuthResponseAsync(
        User user,
        string? ipAddress,
        string? deviceInfo,
        IJwtTokenGenerator jwtGenerator,
        IRefreshTokenRepository refreshTokenRepository,
        IUnitOfWork unitOfWork,
        string defaultRole,
        CancellationToken ct,
        Guid? tokenFamilyId = null)
    {
        var roles = user.GetRoles(defaultRole);
        var (accessToken, refreshToken, expiresAt) = jwtGenerator.GenerateTokens(user, roles);
        // A fresh login/register starts a new rotation family; a refresh carries the
        // presented token's family forward so reuse detection can revoke the whole chain.
        var token = jwtGenerator.CreateRefreshTokenEntity(user.Id, refreshToken, ipAddress, deviceInfo, tokenFamilyId);
        await refreshTokenRepository.AddAsync(token, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, refreshToken, expiresAt, UserMapper.ToDto(user, roles));
    }
}

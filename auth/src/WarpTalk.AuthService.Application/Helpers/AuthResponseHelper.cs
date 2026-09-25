using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces.Security;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared.Authorization;

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
        Guid? tokenFamilyId = null,
        IStaffAccessService? staffAccess = null)
    {
        var roles = user.GetRoles(defaultRole);

        // G10: an ACTIVE staff member's token carries the role "admin" (what the web and the
        // proxy already read as "show the admin portal") and staff_role. Both are hints; every
        // admin endpoint asks the auth service for the live answer. Resolving here is also where
        // a pending staff invitation is accepted, on the invitee's first verified sign-in.
        var staff = await (staffAccess ?? new StaffAccessService(unitOfWork, NullLogger<StaffAccessService>.Instance))
            .ResolveForTokenAsync(user, ct);
        if (staff is not null && !roles.Contains(StaffClaims.StaffRoleHint))
        {
            roles.Add(StaffClaims.StaffRoleHint);
        }

        var (accessToken, refreshToken, expiresAt) = jwtGenerator.GenerateTokens(user, roles, staff?.RoleSlug);
        // A fresh login/register starts a new rotation family; a refresh carries the
        // presented token's family forward so reuse detection can revoke the whole chain.
        var token = jwtGenerator.CreateRefreshTokenEntity(user.Id, refreshToken, ipAddress, deviceInfo, tokenFamilyId);
        await refreshTokenRepository.AddAsync(token, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, refreshToken, expiresAt, UserMapper.ToDto(user, roles));
    }
}

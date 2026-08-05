using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class UserDirectoryService : IUserDirectoryService
{
    // Defaults applied when a user has settings but left a language unset. Kept here
    // rather than at the gRPC boundary so every caller resolves the same value.
    private const string FallbackSpeakLanguage = "vi-VN";
    private const string FallbackListenLanguage = "en-US";

    private readonly IUnitOfWork _unitOfWork;

    public UserDirectoryService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserIdentityDto>> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure<UserIdentityDto>("User not found.", ErrorCodes.UserNotFound);

        return Result.Success(new UserIdentityDto(
            user.Id,
            user.Email,
            user.FullName,
            user.AvatarUrl,
            user.PreferredLanguage));
    }

    public async Task<Result<UserIdentityDto>> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<UserIdentityDto>("Email is required.", ErrorCodes.ValidationError);

        var user = await _unitOfWork.UserRepository.FirstOrDefaultAsync(u => u.Email == email, "", ct);
        if (user is null)
            return Result.Failure<UserIdentityDto>("User not found.", ErrorCodes.UserNotFound);

        return Result.Success(new UserIdentityDto(
            user.Id,
            user.Email,
            user.FullName,
            user.AvatarUrl,
            user.PreferredLanguage));
    }

    public async Task<Result<UserLanguageDefaultsDto?>> GetLanguageDefaultsAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
        if (settings is null)
            return Result.Success<UserLanguageDefaultsDto?>(null);

        return Result.Success<UserLanguageDefaultsDto?>(new UserLanguageDefaultsDto(
            settings.DefaultSpeakLanguage ?? FallbackSpeakLanguage,
            settings.DefaultListenLanguage ?? FallbackListenLanguage));
    }

    public async Task<Result<RoleDto>> GetRoleByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<RoleDto>("Role name is required.", ErrorCodes.ValidationError);

        var role = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == name, "", ct);
        if (role is null)
            return Result.Failure<RoleDto>("Role not found.", ErrorCodes.NotFound);

        return Result.Success(new RoleDto(role.Id, role.Name, role.Description));
    }

    public async Task<Result<RoleDto>> GetRoleByIdAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await _unitOfWork.RoleRepository.GetByIdAsync(roleId, ct);
        if (role is null)
            return Result.Failure<RoleDto>("Role not found.", ErrorCodes.NotFound);

        return Result.Success(new RoleDto(role.Id, role.Name, role.Description));
    }
}

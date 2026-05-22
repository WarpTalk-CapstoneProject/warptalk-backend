using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class ProfileService : IProfileService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthSettings _authSettings;

    public ProfileService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IOptions<AuthSettings> authSettings)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _authSettings = authSettings.Value;
        _userRepository = _unitOfWork.UserRepository;
    }

    public async Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        var status = UserStatusHelper.GetAccountStatus(user);
        if (status is AccountStatus.DISABLED or AccountStatus.LOCKED)
            return UserStatusHelper.CheckUserStatus<UserDto>(user)!;

        return Result.Success(UserMapper.ToDto(user, _authSettings.DefaultRole));
    }

    public async Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        var statusResult = UserStatusHelper.CheckUserStatus<UserDto>(user);
        if (statusResult is not null)
            return statusResult;

        if (request.FullName is not null) user.FullName = request.FullName.Trim();
        if (request.Phone is not null) user.Phone = request.Phone.Trim();
        if (request.PreferredLanguage is not null) user.PreferredLanguage = request.PreferredLanguage;
        if (request.Timezone is not null) user.Timezone = request.Timezone;
        user.UpdatedAt = DateTime.UtcNow;

        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(UserMapper.ToDto(user, _authSettings.DefaultRole));
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        var statusResult = UserStatusHelper.CheckUserStatus<bool>(user);
        if (statusResult is not null)
            return statusResult;

        // allow empty PasswordHash if user was created via Google and has no standard password yet
        if (!string.IsNullOrEmpty(user.PasswordHash) && !_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure(AuthConstants.ErrorInvalidPassword, ErrorCodes.InvalidPassword);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        _userRepository.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

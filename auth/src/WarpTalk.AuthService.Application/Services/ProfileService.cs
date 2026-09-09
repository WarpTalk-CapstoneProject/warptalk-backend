using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthSettings _authSettings;
    private readonly ILogger<ProfileService> _logger;
    /// <summary>
    /// The object store. Named for the first thing that used it, not for what it can hold — it
    /// is a key/stream blob store, and a second interface over the same bucket, the same S3
    /// client and the same local fallback would be three files that differ only in a noun.
    /// </summary>
    private readonly IVoiceSampleStorage _storage;

    public ProfileService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IOptions<AuthSettings> authSettings,
        ILogger<ProfileService> logger,
        IVoiceSampleStorage storage)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _storage = storage;
        _authSettings = authSettings.Value;
        _logger = logger;
        _userRepository = _unitOfWork.UserRepository;
        _refreshTokenRepository = _unitOfWork.RefreshTokenRepository;
    }

    public async Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
            if (user is null || user.DeletedAt is not null)
                return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

            var status = UserStatusHelper.GetAccountStatus(user);
            if (status is AccountStatus.DISABLED or AccountStatus.LOCKED)
                return UserStatusHelper.CheckUserStatus<UserDto>(user)!;

            return Result.Success(UserMapper.ToDto(user, _authSettings.DefaultRole));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching profile. UserId: {UserId}", userId);
            return Result.Failure<UserDto>("An unexpected error occurred while fetching the profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UserDto>> UpdateAvatarAsync(
        Guid userId, Stream content, string? contentType, long length, CancellationToken ct = default)
    {
        var extension = ProfileAvatarContract.ExtensionFor(contentType);
        if (extension is null)
        {
            return Result.Failure<UserDto>(
                "An avatar must be a PNG, JPEG or WebP image.", ErrorCodes.ValidationError);
        }
        if (length <= 0)
        {
            return Result.Failure<UserDto>("The image is empty.", ErrorCodes.ValidationError);
        }
        if (length > ProfileAvatarContract.MaxSizeBytes)
        {
            return Result.Failure<UserDto>(
                "An avatar must be under 2 MB.", ErrorCodes.ValidationError);
        }

        try
        {
            var user = await _userRepository.GetByIdWithRolesAsync(userId, ct);
            if (user is null || user.DeletedAt is not null)
                return Result.Failure<UserDto>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

            var statusResult = UserStatusHelper.CheckUserStatus<UserDto>(user);
            if (statusResult is not null)
                return statusResult;

            // Read once, into memory. The bytes are needed twice — to check what they actually
            // are, and to store them — and a request stream cannot always be rewound. 2 MB is
            // the cap enforced above, so this is bounded before it is buffered.
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            if (!ProfileAvatarContract.LooksLikeImage(bytes))
            {
                // The Content-Type is supplied by whoever is uploading. Storing a file under a
                // name the browser will fetch back, on the strength of a header the uploader
                // chose, is how an image endpoint becomes a file-serving one.
                return Result.Failure<UserDto>(
                    "That file is not a PNG, JPEG or WebP image.", ErrorCodes.ValidationError);
            }

            var key = ProfileAvatarContract.StorageKey(userId, extension);
            using (var upload = new MemoryStream(bytes, writable: false))
            {
                await _storage.SaveAsync(key, upload, ct);
            }

            user.AvatarUrl = ProfileAvatarContract.PublicPath(userId, extension);
            user.UpdatedAt = DateTime.UtcNow;
            _userRepository.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(UserMapper.ToDto(user, _authSettings.DefaultRole));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating the avatar. UserId: {UserId}", userId);
            return Result.Failure<UserDto>(
                "An unexpected error occurred while saving the avatar.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<ProfileAvatarContent>> GetAvatarAsync(
        Guid userId, string extension, CancellationToken ct = default)
    {
        if (!ProfileAvatarContract.ContentTypeByExtension.TryGetValue(extension, out var contentType))
        {
            return Result.Failure<ProfileAvatarContent>("Unknown image type.", ErrorCodes.ValidationError);
        }

        try
        {
            var stream = await _storage.ReadAsync(ProfileAvatarContract.StorageKey(userId, extension), ct);
            return Result.Success(new ProfileAvatarContent(stream, contentType));
        }
        catch (Exception ex)
        {
            // A missing avatar is a 404, not a 500: the row can carry a path whose object was
            // never written, and the caller is an <img> that should simply fall back to initials.
            _logger.LogDebug(ex, "No stored avatar for {UserId}.", userId);
            return Result.Failure<ProfileAvatarContent>("No avatar for this user.", ErrorCodes.NotFound);
        }
    }

    public async Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating profile. UserId: {UserId}", userId);
            return Result.Failure<UserDto>("An unexpected error occurred while updating the profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        try
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

            // Changing a password is how a user evicts someone who got in. It only works if the
            // sessions that someone already holds die with the old password.
            //
            // AuthService.ResetPasswordAsync already did this; the change-password path did not,
            // so a user who noticed a compromise and changed their password stayed compromised:
            // the attacker's stolen refresh token kept rotating into fresh access tokens for the
            // rest of its lifetime, and nothing the victim could do from the UI stopped it.
            await _refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Password changed and all refresh tokens revoked. UserId: {UserId}", userId);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while changing password. UserId: {UserId}", userId);
            return Result.Failure("An unexpected error occurred while changing the password.", ErrorCodes.InternalServerError);
        }
    }
}

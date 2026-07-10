using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class VoiceProfileService : IVoiceProfileService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<VoiceProfileService> _logger;

    public VoiceProfileService(IUnitOfWork unitOfWork, ILogger<VoiceProfileService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<VoiceProfileDto>>> GetProfilesAsync(Guid userId, Guid? workspaceId = null, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureReadableUserAsync<IReadOnlyList<VoiceProfileDto>>(userId, ct);
            if (userResult is not null) return userResult;

            var profiles = await _unitOfWork.VoiceProfileRepository.GetByUserIdAsync(userId, workspaceId, ct);
            return Result.Success<IReadOnlyList<VoiceProfileDto>>(profiles.Select(VoiceProfileMapper.ToDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching voice profiles. UserId: {UserId}", userId);
            return Result.Failure<IReadOnlyList<VoiceProfileDto>>("An unexpected error occurred while fetching voice profiles.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> GetProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureReadableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            return profile is null
                ? Result.Failure<VoiceProfileDto>("Voice profile not found.", ErrorCodes.NotFound)
                : Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching voice profile. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while fetching the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> CreateProfileAsync(Guid userId, CreateVoiceProfileRequest request, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = VoiceProfileMapper.ToEntity(userId, request);
            await _unitOfWork.VoiceProfileRepository.AddAsync(profile, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating voice profile. UserId: {UserId}", userId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while creating the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> UpdateProfileAsync(Guid userId, Guid profileId, UpdateVoiceProfileRequest request, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            if (profile is null)
                return Result.Failure<VoiceProfileDto>("Voice profile not found.", ErrorCodes.NotFound);

            if (request.Status == VoiceProfileConstants.StatusReady)
            {
                var hasConsent = await _unitOfWork.VoiceConsentRepository.HasGrantedConsentAsync(profileId, VoiceProfileConstants.ConsentTypeVoiceClone, ct);
                if (!hasConsent)
                    return Result.Failure<VoiceProfileDto>("Voice consent is required before a profile can be marked ready.", ErrorCodes.ValidationError);
            }

            if (request.DisplayName is not null) profile.DisplayName = request.DisplayName.Trim();
            if (request.Provider is not null) profile.Provider = request.Provider.Trim();
            if (request.EmbeddingRef is not null) profile.EmbeddingRef = request.EmbeddingRef.Trim();
            if (request.Status is not null) profile.Status = request.Status.Trim().ToLowerInvariant();

            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedBy = userId;

            _unitOfWork.VoiceProfileRepository.Update(profile);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating voice profile. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while updating the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> DeleteProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<bool>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            if (profile is null)
                return Result.Failure("Voice profile not found.", ErrorCodes.NotFound);

            profile.IsActive = false;
            profile.Status = VoiceProfileConstants.StatusDisabled;
            profile.DeletedAt = DateTime.UtcNow;
            profile.DeletedBy = userId;
            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedBy = userId;

            _unitOfWork.VoiceProfileRepository.Update(profile);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting voice profile. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure("An unexpected error occurred while deleting the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> AddSampleAsync(Guid userId, Guid profileId, AddVoiceSampleRequest request, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            if (profile is null)
                return Result.Failure<VoiceProfileDto>("Voice profile not found.", ErrorCodes.NotFound);

            var sample = VoiceProfileMapper.ToSample(profileId, userId, request);
            await _unitOfWork.VoiceSampleRepository.AddAsync(sample, ct);

            profile.Samples.Add(sample);
            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedBy = userId;
            _unitOfWork.VoiceProfileRepository.Update(profile);

            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while adding voice sample. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while adding the voice sample.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> GrantConsentAsync(Guid userId, Guid profileId, GrantVoiceConsentRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            if (profile is null)
                return Result.Failure<VoiceProfileDto>("Voice profile not found.", ErrorCodes.NotFound);

            var consent = VoiceProfileMapper.ToGrantedConsent(userId, profileId, request, ipAddress, userAgent);
            await _unitOfWork.VoiceConsentRepository.AddAsync(consent, ct);

            profile.Consents.Add(consent);
            if (profile.Status == VoiceProfileConstants.StatusPendingConsent)
            {
                profile.Status = VoiceProfileConstants.StatusDraft;
            }

            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedBy = userId;
            _unitOfWork.VoiceProfileRepository.Update(profile);

            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while granting voice consent. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while granting voice consent.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> RevokeConsentAsync(Guid userId, Guid profileId, RevokeVoiceConsentRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        try
        {
            var userResult = await EnsureWritableUserAsync<VoiceProfileDto>(userId, ct);
            if (userResult is not null) return userResult;

            var profile = await GetOwnedProfileAsync(userId, profileId, ct);
            if (profile is null)
                return Result.Failure<VoiceProfileDto>("Voice profile not found.", ErrorCodes.NotFound);

            var latestGranted = await _unitOfWork.VoiceConsentRepository.GetLatestAsync(profileId, request.ConsentType.Trim().ToLowerInvariant(), ConsentStatus.GRANTED, ct);
            if (latestGranted is not null && latestGranted.RevokedAt is null)
            {
                latestGranted.ConsentStatus = ConsentStatus.REVOKED;
                latestGranted.RevokedAt = DateTime.UtcNow;
                _unitOfWork.VoiceConsentRepository.Update(latestGranted);
            }

            var revokedConsent = VoiceProfileMapper.ToRevokedConsent(userId, profileId, request, ipAddress, userAgent);
            await _unitOfWork.VoiceConsentRepository.AddAsync(revokedConsent, ct);

            profile.Consents.Add(revokedConsent);
            profile.Status = VoiceProfileConstants.StatusDisabled;
            profile.UpdatedAt = DateTime.UtcNow;
            profile.UpdatedBy = userId;
            _unitOfWork.VoiceProfileRepository.Update(profile);

            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while revoking voice consent. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while revoking voice consent.", ErrorCodes.InternalServerError);
        }
    }

    private async Task<VoiceProfile?> GetOwnedProfileAsync(Guid userId, Guid profileId, CancellationToken ct)
    {
        return await _unitOfWork.VoiceProfileRepository.GetByIdForUserAsync(profileId, userId, ct);
    }

    private async Task<Result<T>?> EnsureReadableUserAsync<T>(Guid userId, CancellationToken ct)
    {
        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure<T>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        var status = UserStatusHelper.GetAccountStatus(user);
        return status is AccountStatus.DISABLED or AccountStatus.LOCKED
            ? UserStatusHelper.CheckUserStatus<T>(user)
            : null;
    }

    private async Task<Result<T>?> EnsureWritableUserAsync<T>(Guid userId, CancellationToken ct)
    {
        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
            return Result.Failure<T>(AuthConstants.ErrorUserNotFound, ErrorCodes.UserNotFound);

        return UserStatusHelper.CheckUserStatus<T>(user);
    }
}

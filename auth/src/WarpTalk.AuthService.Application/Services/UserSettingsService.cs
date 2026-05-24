using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class UserSettingsService : IUserSettingsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UserSettingsService> _logger;

    public UserSettingsService(IUnitOfWork unitOfWork, ILogger<UserSettingsService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<UserSettingsDto>> GetSettingsAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
            if (user == null)
            {
                return Result.Failure<UserSettingsDto>("User not found.", ErrorCodes.UserNotFound);
            }

            var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
            if (settings == null)
            {
                // Self-Healing logic: create default user settings if not exists
                settings = UserSettingsMapper.CreateDefaultUserSettings(userId);

                _unitOfWork.UserSettingRepository.Add(settings);
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return Result.Success(UserSettingsMapper.ToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching user settings. UserId: {UserId}", userId);
            return Result.Failure<UserSettingsDto>("An unexpected error occurred while fetching user settings.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<UserSettingsDto>> UpdateSettingsAsync(Guid userId, UpdateUserSettingsRequest request, CancellationToken ct = default)
    {
        try
        {
            var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
            if (settings == null)
            {
                var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
                if (user == null)
                {
                    return Result.Failure<UserSettingsDto>("User not found.", ErrorCodes.UserNotFound);
                }

                // Provision settings on the fly
                settings = UserSettingsMapper.CreateDefaultUserSettings(userId);
                _unitOfWork.UserSettingRepository.Add(settings);
            }

            // --- Validation is pre-handled by FluentValidation at API layer ---

            if (request.TranscriptFontSize.HasValue)
            {
                settings.TranscriptFontSize = request.TranscriptFontSize.Value;
            }

            if (request.DefaultMaxParticipants.HasValue)
            {
                settings.DefaultMaxParticipants = request.DefaultMaxParticipants.Value;
            }

            if (request.Theme != null)
            {
                settings.Theme = request.Theme.ToLowerInvariant();
            }

            if (request.DefaultTranslationRoomType != null)
            {
                settings.DefaultTranslationRoomType = request.DefaultTranslationRoomType.ToLowerInvariant();
            }

            if (request.DefaultSpeakLanguage != null)
            {
                settings.DefaultSpeakLanguage = request.DefaultSpeakLanguage;
            }

            if (request.DefaultListenLanguage != null)
            {
                settings.DefaultListenLanguage = request.DefaultListenLanguage;
            }

            // Update optional/boolean values
            if (request.VoiceCloneEnabled.HasValue)
                settings.VoiceCloneEnabled = request.VoiceCloneEnabled.Value;

            if (request.MicNoiseSuppression.HasValue)
                settings.MicNoiseSuppression = request.MicNoiseSuppression.Value;

            if (request.AutoRecordTranslationRooms.HasValue)
                settings.AutoRecordTranslationRooms = request.AutoRecordTranslationRooms.Value;

            if (request.AutoGenerateSummary.HasValue)
                settings.AutoGenerateSummary = request.AutoGenerateSummary.Value;

            if (request.ShowOriginalTranscript.HasValue)
                settings.ShowOriginalTranscript = request.ShowOriginalTranscript.Value;

            if (request.ShowTranslatedTranscript.HasValue)
                settings.ShowTranslatedTranscript = request.ShowTranslatedTranscript.Value;

            if (request.HighContrast.HasValue)
                settings.HighContrast = request.HighContrast.Value;

            if (request.ScreenReaderMode.HasValue)
                settings.ScreenReaderMode = request.ScreenReaderMode.Value;

            settings.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.UserSettingRepository.Update(settings);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(UserSettingsMapper.ToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating user settings. UserId: {UserId}", userId);
            return Result.Failure<UserSettingsDto>("An unexpected error occurred while updating user settings.", ErrorCodes.InternalServerError);
        }
    }
}

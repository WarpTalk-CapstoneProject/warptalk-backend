using System;
using System.Collections.Generic;
using System.IO;
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

public class VoiceProfileService : IVoiceProfileService
{
    private const long MaxSampleSizeBytes = 20 * 1024 * 1024; // 20 MB
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav", "audio/x-wav", "audio/mpeg", "audio/mp3", "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/ogg", "audio/webm"
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IVoiceSampleStorage _storage;
    private readonly ILogger<VoiceProfileService> _logger;

    public VoiceProfileService(IUnitOfWork unitOfWork, IVoiceSampleStorage storage, ILogger<VoiceProfileService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<VoiceProfileDto>>> GetProfilesAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var profiles = await _unitOfWork.VoiceProfileRepository.GetByUserIdAsync(userId, ct);
            var dtos = new List<VoiceProfileDto>();
            foreach (var profile in profiles)
            {
                dtos.Add(VoiceProfileMapper.ToDto(profile));
            }
            return Result.Success<IReadOnlyList<VoiceProfileDto>>(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching voice profiles. UserId: {UserId}", userId);
            return Result.Failure<IReadOnlyList<VoiceProfileDto>>("An unexpected error occurred while fetching voice profiles.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VoiceProfileDto>> CreateProfileAsync(Guid userId, CreateVoiceProfileRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Result.Failure<VoiceProfileDto>("Display name is required.", ErrorCodes.ValidationError);
        }

        if (string.IsNullOrWhiteSpace(request.Language))
        {
            return Result.Failure<VoiceProfileDto>("Language is required.", ErrorCodes.ValidationError);
        }

        if (request.Sample != null)
        {
            if (request.Sample.Length <= 0)
            {
                return Result.Failure<VoiceProfileDto>("The voice sample file is empty.", ErrorCodes.ValidationError);
            }
            if (request.Sample.Length > MaxSampleSizeBytes)
            {
                return Result.Failure<VoiceProfileDto>("The voice sample file exceeds the 20 MB limit.", ErrorCodes.ValidationError);
            }
            if (!AllowedContentTypes.Contains(request.Sample.ContentType))
            {
                return Result.Failure<VoiceProfileDto>("Unsupported audio format.", ErrorCodes.ValidationError);
            }
        }

        try
        {
            var profile = new VoiceProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DisplayName = request.DisplayName.Trim(),
                Language = request.Language,
                Status = "active",
                IsActive = true,
                CreatedBy = userId,
                UpdatedBy = userId,
            };

            VoiceSample? sample = null;
            string? storageKey = null;

            if (request.Sample != null)
            {
                var extension = Path.GetExtension(request.Sample.FileName);
                storageKey = $"{userId}/{profile.Id}{extension}";

                using (var stream = request.Sample.OpenReadStream())
                {
                    await _storage.SaveAsync(storageKey, stream, ct);
                }

                sample = new VoiceSample
                {
                    Id = Guid.NewGuid(),
                    VoiceProfileId = profile.Id,
                    SampleType = "reference",
                    FileUrl = storageKey,
                    Language = request.Language,
                    ContainsRawAudio = true,
                };
            }

            try
            {
                _unitOfWork.VoiceProfileRepository.Add(profile);
                if (sample != null)
                {
                    await _unitOfWork.Repository<VoiceSample>().AddAsync(sample, ct);
                }
                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch
            {
                if (storageKey != null)
                {
                    await _storage.DeleteAsync(storageKey, ct);
                }
                throw;
            }

            profile.VoiceSamples = sample != null ? new List<VoiceSample> { sample } : new List<VoiceSample>();
            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating voice profile. UserId: {UserId}", userId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while creating the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> DeleteProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var profile = await _unitOfWork.VoiceProfileRepository.GetByIdForUserAsync(profileId, userId, ct);
            if (profile == null)
            {
                return Result.Failure("Voice profile not found.", ErrorCodes.NotFound);
            }

            var now = DateTime.UtcNow;
            profile.DeletedAt = now;
            profile.DeletedBy = userId;
            profile.IsActive = false;
            profile.UpdatedAt = now;
            profile.UpdatedBy = userId;

            _unitOfWork.VoiceProfileRepository.Update(profile);
            await _unitOfWork.SaveChangesAsync(ct);

            foreach (var sample in profile.VoiceSamples)
            {
                if (!string.IsNullOrEmpty(sample.FileUrl))
                {
                    await _storage.DeleteAsync(sample.FileUrl, ct);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while deleting voice profile. UserId: {UserId}, ProfileId: {ProfileId}", userId, profileId);
            return Result.Failure("An unexpected error occurred while deleting the voice profile.", ErrorCodes.InternalServerError);
        }
    }
}

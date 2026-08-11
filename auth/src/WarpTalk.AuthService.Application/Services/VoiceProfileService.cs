using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// The only provider a picked library voice can currently come from. Stored on the
    /// profile so a future second provider can coexist without guessing what an
    /// EmbeddingRef belongs to.
    /// </summary>
    private const string LibraryVoiceProvider = "cartesia";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IVoiceSampleStorage _storage;
    private readonly IVoiceCatalogDirectory _voiceCatalog;
    private readonly ILogger<VoiceProfileService> _logger;

    public VoiceProfileService(
        IUnitOfWork unitOfWork,
        IVoiceSampleStorage storage,
        IVoiceCatalogDirectory voiceCatalog,
        ILogger<VoiceProfileService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _voiceCatalog = voiceCatalog;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<VoiceCatalogItemDto>>> GetCatalogAsync(string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Result.Failure<IReadOnlyList<VoiceCatalogItemDto>>("Language is required.", ErrorCodes.ValidationError);
        }

        var voices = await _voiceCatalog.GetAsync(language, ct);
        return Result.Success(voices);
    }

    public async Task<Result<VoiceProfileDto?>> SetPreferredVoiceAsync(Guid userId, SetPreferredVoiceRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Language))
        {
            return Result.Failure<VoiceProfileDto?>("Language is required.", ErrorCodes.ValidationError);
        }

        var language = request.Language.Trim();
        var voiceId = request.VoiceId?.Trim();
        var clearing = string.IsNullOrEmpty(voiceId);

        try
        {
            // Reject an id that is not actually on offer for this language. Without this the
            // stored preference would be round-tripped into SetVoicePreference and silently
            // produce the wrong voice — or none — deep inside the TTS worker.
            if (!clearing)
            {
                var catalog = await _voiceCatalog.GetAsync(language, ct);
                if (catalog.Count == 0)
                {
                    return Result.Failure<VoiceProfileDto?>(
                        "No voices are available for this language yet.",
                        ErrorCodes.InvalidState);
                }
                if (!catalog.Any(v => string.Equals(v.Id, voiceId, StringComparison.Ordinal)))
                {
                    return Result.Failure<VoiceProfileDto?>(
                        "That voice is not offered for this language.",
                        ErrorCodes.ValidationError);
                }
            }

            var profiles = await _unitOfWork.VoiceProfileRepository.GetByUserIdAsync(userId, ct);
            var existing = profiles.FirstOrDefault(p =>
                string.Equals(p.Provider, LibraryVoiceProvider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase));

            var now = DateTime.UtcNow;

            if (clearing)
            {
                if (existing == null)
                {
                    // Already no preference — clearing twice is not an error.
                    return Result.Success<VoiceProfileDto?>(null);
                }

                existing.DeletedAt = now;
                existing.DeletedBy = userId;
                existing.IsActive = false;
                existing.UpdatedAt = now;
                existing.UpdatedBy = userId;
                _unitOfWork.VoiceProfileRepository.Update(existing);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success<VoiceProfileDto?>(null);
            }

            if (existing != null)
            {
                existing.EmbeddingRef = voiceId;
                existing.IsActive = true;
                existing.Status = "active";
                existing.UpdatedAt = now;
                existing.UpdatedBy = userId;
                _unitOfWork.VoiceProfileRepository.Update(existing);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success<VoiceProfileDto?>(VoiceProfileMapper.ToDto(existing));
            }

            var created = new VoiceProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DisplayName = null,
                Language = language,
                Provider = LibraryVoiceProvider,
                EmbeddingRef = voiceId,
                Status = "active",
                IsActive = true,
                CreatedBy = userId,
                UpdatedBy = userId,
            };

            _unitOfWork.VoiceProfileRepository.Add(created);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success<VoiceProfileDto?>(VoiceProfileMapper.ToDto(created));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while setting preferred voice. UserId: {UserId}, Language: {Language}", userId, language);
            return Result.Failure<VoiceProfileDto?>("An unexpected error occurred while saving the preferred voice.", ErrorCodes.InternalServerError);
        }
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

    public async Task<Result<VoiceProfileDto>> CreateProfileAsync(
        Guid userId,
        CreateVoiceProfileRequest request,
        CancellationToken ct = default,
        string? ipAddress = null,
        string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Result.Failure<VoiceProfileDto>("Display name is required.", ErrorCodes.ValidationError);
        }

        if (string.IsNullOrWhiteSpace(request.Language))
        {
            return Result.Failure<VoiceProfileDto>("Language is required.", ErrorCodes.ValidationError);
        }

        if (request.Sample == null)
        {
            return Result.Failure<VoiceProfileDto>(
                "A validated voice sample is required.",
                ErrorCodes.ValidationError);
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

        if (!HasRequiredVoiceProfileConsent(request))
        {
            return Result.Failure<VoiceProfileDto>(
                "Voice consent is required before saving this voice profile.",
                ErrorCodes.ValidationError);
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
            VoiceConsent? consent = null;
            string? storageKey = null;
            var now = DateTime.UtcNow;

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

                consent = new VoiceConsent
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    VoiceProfileId = profile.Id,
                    ConsentType = VoiceProfileConsentContract.UploadConsentType,
                    ConsentStatus = VoiceProfileConsentContract.GrantedStatus,
                    ConsentTextVersion = VoiceProfileConsentContract.Version,
                    GrantedAt = now,
                    IpAddress = NullIfWhiteSpace(ipAddress, 45),
                    UserAgent = NullIfWhiteSpace(userAgent, 500),
                    ContractSnapshot = VoiceProfileConsentContract.Snapshot,
                    ContractHash = VoiceProfileConsentContract.SnapshotHash(),
                    OwnVoiceConfirmed = request.OwnVoiceConfirmed,
                    AiUseConfirmed = request.AiUseConfirmed,
                    SyntheticVoiceAcknowledged = request.SyntheticVoiceAcknowledged,
                    NoImpersonationConfirmed = request.NoImpersonationConfirmed,
                    RetentionAcknowledged = request.RetentionAcknowledged,
                };
            }

            try
            {
                _unitOfWork.VoiceProfileRepository.Add(profile);
                if (sample != null)
                {
                    await _unitOfWork.VoiceSampleRepository.AddAsync(sample, ct);
                }
                if (consent != null)
                {
                    await _unitOfWork.VoiceConsentRepository.AddAsync(consent, ct);
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
            profile.VoiceConsents = consent != null ? new List<VoiceConsent> { consent } : new List<VoiceConsent>();
            return Result.Success(VoiceProfileMapper.ToDto(profile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating voice profile. UserId: {UserId}", userId);
            return Result.Failure<VoiceProfileDto>("An unexpected error occurred while creating the voice profile.", ErrorCodes.InternalServerError);
        }
    }

    private static bool HasRequiredVoiceProfileConsent(CreateVoiceProfileRequest request)
        => request.OwnVoiceConfirmed
           && request.AiUseConfirmed
           && request.SyntheticVoiceAcknowledged
           && request.NoImpersonationConfirmed
           && request.RetentionAcknowledged;

    private static string? NullIfWhiteSpace(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
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

            // The sample rows go in the SAME unit of work as the profile. Soft-deleting only
            // the profile left every voice_samples row with deleted_at = NULL pointing at a
            // file_url whose object had already been removed below — the row claimed the
            // sample was live while the bucket said it was gone (WT-276).
            var storageKeys = new List<string>();
            foreach (var sample in profile.VoiceSamples)
            {
                sample.DeletedAt = now;
                sample.DeletedBy = userId;
                _unitOfWork.VoiceSampleRepository.Update(sample);

                if (!string.IsNullOrEmpty(sample.FileUrl))
                {
                    storageKeys.Add(sample.FileUrl);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // Ordering: commit the rows first, remove the objects after — the mirror of
            // CreateProfileAsync, which writes the object first and deletes it again if the
            // database write fails. Both orders leak in one direction, and we deliberately
            // prefer the same direction the create path already prefers: an orphaned OBJECT
            // (rows soft-deleted, bytes still in the bucket) over an orphaned ROW (bytes gone,
            // row still claiming the sample is live). An orphaned object only costs storage and
            // is invisible to readers; an orphaned row is read back and believed, which is
            // precisely the defect being fixed here.
            foreach (var storageKey in storageKeys)
            {
                try
                {
                    await _storage.DeleteAsync(storageKey, ct);
                }
                catch (Exception ex)
                {
                    // A partial storage failure is not a failed delete. The rows are already
                    // committed as deleted, so the profile is gone as far as every reader is
                    // concerned and a retry could only return NotFound. Log the leaked key,
                    // keep deleting the remaining samples, and still report success — the
                    // alternative is telling the caller the delete failed when the database
                    // says otherwise.
                    _logger.LogWarning(
                        ex,
                        "Voice sample object was left behind after its profile was deleted. UserId: {UserId}, ProfileId: {ProfileId}, StorageKey: {StorageKey}",
                        userId,
                        profileId,
                        storageKey);
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

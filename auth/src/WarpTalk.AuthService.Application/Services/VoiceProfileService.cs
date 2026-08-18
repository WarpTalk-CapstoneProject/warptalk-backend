using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
    /// The media type alone, with any parameters stripped — <c>audio/webm;codecs=opus</c> becomes
    /// <c>audio/webm</c>.
    /// </summary>
    /// <remarks>
    /// WT-372. The allowlist above holds bare types, and the check compared the WHOLE
    /// <c>Content-Type</c> header against it. A <c>Content-Type</c> is a media type plus optional
    /// parameters (RFC 9110 §8.3), and browsers send them: <c>MediaRecorder</c> is constructed
    /// with <c>audio/webm;codecs=opus</c> and reports that string back, so the recorded file
    /// reaches this service as <c>audio/webm;codecs=opus</c> and misses <c>audio/webm</c> by the
    /// suffix. Recording therefore failed with "Unsupported audio format." on every browser,
    /// while UPLOADING a .wav worked — a file picked off disk carries a bare <c>audio/wav</c>.
    ///
    /// Fixed here rather than by stripping the parameter in the web client: the parameter is
    /// legal, every conforming client may send one, and the desktop app and Safari
    /// (<c>audio/mp4;codecs=mp4a.40.2</c>) would each have hit the same wall separately.
    /// </remarks>
    private static string MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var separator = contentType.IndexOf(';');
        return (separator < 0 ? contentType : contentType[..separator]).Trim();
    }

    /// <summary>
    /// The only provider a picked library voice can currently come from. Stored on the
    /// profile so a future second provider can coexist without guessing what an
    /// EmbeddingRef belongs to.
    /// </summary>
    private const string LibraryVoiceProvider = "cartesia";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IVoiceSampleStorage _storage;
    private readonly IVoiceCatalogDirectory _voiceCatalog;
    private readonly IVoiceCloneRequestQueue _cloneQueue;
    private readonly IVoicePreviewQueue _previewQueue;
    private readonly ILogger<VoiceProfileService> _logger;

    public VoiceProfileService(
        IUnitOfWork unitOfWork,
        IVoiceSampleStorage storage,
        IVoiceCatalogDirectory voiceCatalog,
        IVoiceCloneRequestQueue cloneQueue,
        IVoicePreviewQueue previewQueue,
        ILogger<VoiceProfileService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _voiceCatalog = voiceCatalog;
        _cloneQueue = cloneQueue;
        _previewQueue = previewQueue;
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

    public async Task<Result<string?>> GetDubVoiceAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
        var chosen = settings?.DubVoiceId;
        return Result.Success(string.IsNullOrWhiteSpace(chosen) ? null : chosen);
    }

    public async Task<Result<(string? VoiceId, decimal? Score)>> GetAutoCloneVoiceAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Ordered best-likeness-first by the repository, so "first" is the answer rather than an
        // accident of insertion order. A voice that has no provider id is skipped rather than
        // returned empty: it would read as "no carried clone" anyway, and skipping lets a second,
        // usable row answer instead.
        var profiles = await _unitOfWork.VoiceProfileRepository.GetAutoClonesAsync(userId, ct);
        foreach (var profile in profiles)
        {
            if (!string.IsNullOrWhiteSpace(profile.EmbeddingRef))
            {
                return Result.Success<(string?, decimal?)>((profile.EmbeddingRef, profile.QualityScore));
            }
        }

        return Result.Success<(string?, decimal?)>((null, null));
    }

    public async Task<Result<string?>> SetDubVoiceAsync(Guid userId, SetDubVoiceRequest request, CancellationToken ct = default)
    {
        var voiceId = request.VoiceId?.Trim();
        var clearing = string.IsNullOrEmpty(voiceId);

        var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
        if (settings is null)
        {
            return Result.Failure<string?>("No settings for this user.", ErrorCodes.NotFound);
        }

        if (!clearing && !await IsVoiceChoosableByAsync(userId, voiceId!, request.Language, ct))
        {
            // Refused here rather than stored and discovered later. An id Cartesia does not know
            // produces no error anywhere: the dub simply comes back in some other voice, which is
            // indistinguishable from this feature not working — the report that started WT-396.
            return Result.Failure<string?>(
                "That voice is not one you can be dubbed in.",
                ErrorCodes.ValidationError);
        }

        settings.DubVoiceId = clearing ? null : voiceId;
        settings.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Dub voice {Action} for user {UserId}", clearing ? "cleared" : "set", userId);

        return Result.Success(settings.DubVoiceId);
    }

    /// <summary>
    /// Whether this user may be dubbed in this voice: it is either on public offer, or it is the
    /// provider voice behind one of their OWN profiles.
    ///
    /// The second half is what lets an uploaded recording be chosen. It is scoped to the caller's
    /// own profiles on purpose — a voice cloned from somebody's recording is theirs, and being
    /// able to name another person's voice id would be a way to be dubbed as them.
    /// </summary>
    private async Task<bool> IsVoiceChoosableByAsync(
        Guid userId, string voiceId, string? language, CancellationToken ct)
    {
        var profiles = await _unitOfWork.VoiceProfileRepository.GetByUserIdAsync(userId, ct);
        if (profiles.Any(p =>
                p.DeletedAt == null
                && string.Equals(p.EmbeddingRef, voiceId, StringComparison.Ordinal)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        var catalog = await _voiceCatalog.GetAsync(language.Trim(), ct);
        return catalog.Any(v => string.Equals(v.Id, voiceId, StringComparison.Ordinal));
    }

    public async Task<Result<byte[]>> PreviewVoiceAsync(
        Guid userId, PreviewVoiceRequest request, CancellationToken ct = default)
    {
        var voiceId = request.VoiceId?.Trim();
        var language = request.Language?.Trim();

        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return Result.Failure<byte[]>("A voice is required.", ErrorCodes.ValidationError);
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            // Not a formality. The sample is SPOKEN in this language, and a voice is a different
            // judgement in one language than another — which is the whole point of listening.
            return Result.Failure<byte[]>("A language is required.", ErrorCodes.ValidationError);
        }

        // The same gate SetDubVoiceAsync applies, deliberately reused rather than restated: a
        // voice cloned from somebody's recording is theirs, and rendering audio from an id this
        // user cannot be dubbed in would be a way to sample another person's voice.
        if (!await IsVoiceChoosableByAsync(userId, voiceId, language, ct))
        {
            return Result.Failure<byte[]>(
                "That voice is not one you can preview.",
                ErrorCodes.ValidationError);
        }

        try
        {
            // Asked for before it is requested. The AI side keys a rendered sample by
            // (voice, language) rather than by request, so every play after the first is a cache
            // read — and the common case never reaches the queue or the timeout below at all.
            var cached = await _previewQueue.TryGetAsync(voiceId, language, ct);
            if (cached is not null)
            {
                return AsResult(cached);
            }

            if (!await _previewQueue.RequestAsync(voiceId, language, ct))
            {
                // Said plainly rather than waited out. Nobody asked for this render, so no answer
                // is coming and holding the request open for the timeout would only look broken.
                return Result.Failure<byte[]>(
                    "Voice previews are unavailable right now.",
                    ErrorCodes.InvalidState);
            }

            var rendered = await _previewQueue.WaitAsync(voiceId, language, ct);
            if (rendered is null)
            {
                // A real outcome, not a failure of the render: it may still land, and the next
                // press of the button is served from the cache instantly. Worded so that trying
                // again reads as the sensible next step, because it is.
                return Result.Failure<byte[]>(
                    "The preview is taking longer than expected. Try again in a moment.",
                    ErrorCodes.InvalidState);
            }

            return AsResult(rendered);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error occurred while previewing voice {VoiceId} for user {UserId}.", voiceId, userId);
            return Result.Failure<byte[]>(
                "An unexpected error occurred while rendering the preview.",
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// A rendered failure is an answer, and is reported as the named failure it is rather than
    /// as an empty success — a play button that silently plays nothing is the state this whole
    /// feature exists to remove.
    /// </summary>
    private static Result<byte[]> AsResult(VoicePreview preview) =>
        preview.Audio is { Length: > 0 }
            ? Result.Success(preview.Audio)
            : Result.Failure<byte[]>(
                preview.Error ?? "The preview could not be rendered.",
                ErrorCodes.InvalidState);

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

    /// <summary>
    /// Read the uploaded recording back and queue it for cloning.
    ///
    /// Read back rather than kept in memory from the upload: the request stream was already
    /// consumed once to write it to storage, and rewinding an IFormFile's stream is only
    /// sometimes possible depending on how ASP.NET buffered it. Storage is the one place the
    /// bytes are definitely intact.
    /// </summary>
    private async Task QueueForCloningAsync(
        VoiceProfile profile, Guid userId, IFormFile uploaded, CancellationToken ct)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var stream = uploaded.OpenReadStream())
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                await stream.CopyToAsync(buffer, ct);
            }

            // Language is nullable on the entity and the AI side reads it as a plain string.
            // Sending null would reach Cartesia as a missing field rather than a default, so an
            // unlabelled recording would fail to clone for a reason nobody could see from here.
            var queued = await _cloneQueue.RequestAsync(
                profile.Id, userId, profile.Language ?? "en", buffer.ToArray(), ct);

            if (!queued)
            {
                _logger.LogWarning(
                    "Voice profile {ProfileId} was stored but not queued for cloning; it stays unusable until re-uploaded.",
                    profile.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue voice profile {ProfileId} for cloning.", profile.Id);
        }
    }

    /// <summary>
    /// Pick up any clone the AI side has finished since the last time this user looked.
    ///
    /// This is where the round trip closes. Only profiles still waiting for a voice are asked
    /// about, so a user with a settled set of profiles costs nothing, and the answer is deleted
    /// as it is read — collecting the same result twice would rewrite a value that is already
    /// stored and log a second time for one event.
    ///
    /// Nothing here may throw. A user opening the Voice Profiles page must see their profiles
    /// whether or not Redis is reachable; the clone simply stays pending, which is a state the
    /// page already renders.
    /// </summary>
    private async Task<int> CollectFinishedClonesAsync(
        IReadOnlyList<VoiceProfile> profiles, CancellationToken ct)
    {
        var collected = 0;

        foreach (var profile in profiles)
        {
            if (profile.DeletedAt != null || !string.IsNullOrWhiteSpace(profile.EmbeddingRef))
            {
                continue;
            }

            VoiceCloneOutcome? outcome;
            try
            {
                outcome = await _cloneQueue.TakeOutcomeAsync(profile.Id, ct);
            }
            catch (Exception ex)
            {
                // The Redis implementation swallows its own failures, but the guard belongs
                // here and not only there: this is a side errand on the path that lists
                // somebody's profiles, and no side errand may decide whether they can see them.
                // Without it, Redis being unreachable turns the Voice Profiles page into an
                // error — losing a feature AND the page it lives on.
                _logger.LogWarning(
                    ex, "Could not collect a clone result for profile {ProfileId}.", profile.Id);
                continue;
            }

            if (outcome is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(outcome.VoiceId))
            {
                profile.EmbeddingRef = outcome.VoiceId;
                profile.Provider = outcome.Provider ?? LibraryVoiceProvider;
                profile.Status = "active";
                profile.IsActive = true;
            }
            else
            {
                // A named failure, not silence. "clone_failed" is what lets the page say the
                // recording could not be turned into a voice instead of listing it as active
                // and leaving somebody to discover in a meeting that it was never usable —
                // which is the whole of WT-396.
                profile.Status = "clone_failed";
                profile.IsActive = false;
                _logger.LogWarning(
                    "Voice clone failed for profile {ProfileId}: {Error}",
                    profile.Id, outcome.Error ?? "no reason given");
            }

            profile.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.VoiceProfileRepository.Update(profile);
            collected++;
        }

        if (collected > 0)
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return collected;
    }

    public async Task<Result<IReadOnlyList<VoiceProfileDto>>> GetProfilesAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var profiles = await _unitOfWork.VoiceProfileRepository.GetByUserIdAsync(userId, ct);
            var collected = await CollectFinishedClonesAsync(profiles, ct);
            var activeConsent = await _unitOfWork.VoiceConsentRepository.GetCurrentAsync(
                userId, VoiceProfileConsentContract.UploadConsentType, ct);

            var dtos = new List<VoiceProfileDto>();
            foreach (var profile in profiles)
            {
                dtos.Add(VoiceProfileMapper.ToDto(profile, activeConsent));
            }

            if (collected > 0)
            {
                _logger.LogInformation(
                    "Collected {Count} finished voice clone(s) for user {UserId}.", collected, userId);
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
            if (!AllowedContentTypes.Contains(MediaTypeOf(request.Sample.ContentType)))
            {
                return Result.Failure<VoiceProfileDto>("Unsupported audio format.", ErrorCodes.ValidationError);
            }
        }

        if (!VoiceProfileConsentContract.IsValidConsentRequest(request))
        {
            return Result.Failure<VoiceProfileDto>(
                "Voice consent is required before saving this voice profile.",
                ErrorCodes.ValidationError);
        }

        byte[] audioBytes;
        using (var ms = new MemoryStream())
        {
            using var readStream = request.Sample!.OpenReadStream();
            await readStream.CopyToAsync(ms, ct);
            audioBytes = ms.ToArray();
        }

        if (!VoiceProfileConsentContract.ValidateAudioMagicBytes(audioBytes))
        {
            return Result.Failure<VoiceProfileDto>(
                "Invalid or corrupted audio file signature.",
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

                using (var writeStream = new MemoryStream(audioBytes))
                {
                    await _storage.SaveAsync(storageKey, writeStream, ct);
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
                    ConsentTextVersion = VoiceProfileConsentContract.ComputeVersionWithAudioHash(audioBytes),
                    GrantedAt = now,
                    CreatedAt = now,
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

            // WT-396 — hand the recording over to be turned into an actual voice.
            //
            // Until now this method ended here: bytes in a bucket, a row marked "active", and
            // nothing anywhere that could make a voice out of them. The profile appeared in the
            // UI as ready and the dub still came back in a stock catalogue voice, because the
            // only voice the pipeline ever looked for was one cloned live from a meeting.
            //
            // AFTER the transaction commits, deliberately. Queueing first would let a worker
            // finish and write an answer for a profile row that then failed to save, leaving a
            // clone nobody owns and a paid provider call for nothing.
            //
            // Failure here is not failure of the upload. The recording is stored and the profile
            // is theirs; what is missing is the voice, and the page shows a recording that is not
            // yet usable rather than an upload that appears to have gone wrong after it worked.
            if (sample != null && request.Sample != null)
            {
                await QueueForCloningAsync(profile, userId, request.Sample, ct);
            }

            return Result.Success(VoiceProfileMapper.ToDto(profile, consent));
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

            // WT-396. If this was the voice they had chosen to be dubbed in, the choice goes with
            // it — in the SAME unit of work, for the same reason the samples do. Left behind, it
            // names a voice whose profile no longer exists: Cartesia answers an unknown id by
            // dubbing them in something else, which reads as the feature being broken rather than
            // as a profile having been deleted.
            if (!string.IsNullOrWhiteSpace(profile.EmbeddingRef))
            {
                var settings = await _unitOfWork.UserSettingRepository.GetByUserIdAsync(userId, ct);
                if (settings is not null
                    && string.Equals(settings.DubVoiceId, profile.EmbeddingRef, StringComparison.Ordinal))
                {
                    settings.DubVoiceId = null;
                    settings.UpdatedAt = now;
                }
            }

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomAudioRouteService : ITranslationRoomAudioRouteService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _translationRoomParticipantRepository;
    private readonly ITranslationRoomAudioRouteRepository _translationRoomAudioRouteRepository;
    private readonly IAudioRouteCacheService _audioRouteCacheService;
    private readonly IAudioRouteEventProcessor _eventProcessor;
    private readonly ILanguagePolicy _languagePolicy;
    private readonly IVoiceConsentDirectory _voiceConsentDirectory;
    private readonly ILogger<TranslationRoomAudioRouteService> _logger;

    public TranslationRoomAudioRouteService(
        IUnitOfWork unitOfWork,
        IAudioRouteCacheService audioRouteCacheService,
        IAudioRouteEventProcessor eventProcessor,
        ILanguagePolicy languagePolicy,
        IVoiceConsentDirectory voiceConsentDirectory,
        ILogger<TranslationRoomAudioRouteService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _translationRoomParticipantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _translationRoomAudioRouteRepository = _unitOfWork.TranslationRoomAudioRouteRepository;
        _audioRouteCacheService = audioRouteCacheService;
        _eventProcessor = eventProcessor;
        _languagePolicy = languagePolicy;
        _voiceConsentDirectory = voiceConsentDirectory;
        _logger = logger;
    }

    public async Task<Result<List<TranslationRoomAudioRouteDto>>> GenerateRoutesAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            // Guard clause: Room lacking data/policy
            if (string.IsNullOrWhiteSpace(room.SourceLanguage) || string.IsNullOrWhiteSpace(room.TargetLanguages))
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorRoomPolicyIncomplete, ErrorCodes.InvalidState);
            }

            var participants = await _translationRoomParticipantRepository.GetByRoomIdAsync(roomId, ct);
            if (participants == null || !participants.Any())
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorNoParticipantsInRoom, ErrorCodes.InvalidState);
            }

            var existingRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var updatedRoutes = new List<TranslationRoomAudioRoute>();
            var newRoutes = new List<TranslationRoomAudioRoute>();

            var sourceLanguage = room.SourceLanguage;
            var targetLanguagesList = room.TargetLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Generate full-mesh audio routing pathways
            foreach (var speaker in participants)
            {
                foreach (var listener in participants)
                {
                    if (speaker.Id == listener.Id) continue;

                    var sourceLang = speaker.SpeakLanguage ?? sourceLanguage;
                    var targetLang = listener.ListenLanguage ?? targetLanguagesList.FirstOrDefault(l => l != sourceLang) ?? sourceLanguage;

                    if (!_languagePolicy.IsTranslationRequired(sourceLang, targetLang)) continue; // Direct audio routing handles same languages bypasses MT

                    var existingRoute = existingRoutes.FirstOrDefault(r =>
                        r.SourceParticipantId == speaker.Id &&
                        r.TargetParticipantId == listener.Id);

                    if (existingRoute != null)
                    {
                        bool isRouteStale = existingRoute.SourceLanguage != sourceLang || existingRoute.TargetLanguage != targetLang;
                        if (isRouteStale)
                        {
                            existingRoute.SourceLanguage = sourceLang;
                            existingRoute.TargetLanguage = targetLang;
                            existingRoute.UpdatedAt = DateTime.UtcNow;
                            updatedRoutes.Add(existingRoute);
                        }
                    }
                    else
                    {
                        var route = new TranslationRoomAudioRoute
                        {
                            Id = Guid.NewGuid(),
                            TranslationRoomId = roomId,
                            SourceParticipantId = speaker.Id,
                            TargetParticipantId = listener.Id,
                            SourceLanguage = sourceLang,
                            TargetLanguage = targetLang,
                            // Default to false per policy — matches TranslationRoomAudioRouteMapper.
                            // ToEntity's documented default. Voice cloning is opt-in (biometric
                            // data): the speaker must explicitly enable it via ToggleVoiceCloneAsync
                            // before tts_worker is allowed to clone their voice.
                            VoiceCloneEnabled = false,
                            Status = AudioRouteStatus.PENDING.ToString(),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        newRoutes.Add(route);
                    }
                }
            }

            // Remove obsolete routes (e.g. participants left the room)
            var activeParticipantIds = participants.Select(p => p.Id).ToHashSet();
            var obsoleteRoutes = existingRoutes
                .Where(r => !activeParticipantIds.Contains(r.SourceParticipantId) || !activeParticipantIds.Contains(r.TargetParticipantId))
                .ToList();

            if (newRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.AddRoutesAsync(newRoutes, ct);
            }

            if (updatedRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.UpdateRoutesAsync(updatedRoutes, ct);
            }

            if (obsoleteRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.RemoveRoutesAsync(obsoleteRoutes, ct);
            }

            if (newRoutes.Any() || updatedRoutes.Any() || obsoleteRoutes.Any())
            {
                await _unitOfWork.SaveChangesAsync(ct);
                await _audioRouteCacheService.PublishRoutesUpdateAsync(roomId, ct);
            }

            var allRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var dtos = allRoutes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList();

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating audio routing mesh for Room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// S7 — add only the routes a single newly-joined participant needs, leaving every other
    /// pair in the room untouched.
    ///
    /// Routes used to be generated exactly once, inside StartTranslationRoomAsync. Nothing on
    /// the join path ever generated any (the comment there claimed "additional routes are
    /// generated as more participants join" — no code path did that), and restarting did not
    /// help because StartTranslationRoomAsync returns early for a room that is already
    /// IN_PROGRESS. Translation and TTS still worked for a late joiner, because the AI re-reads
    /// the live languages hash per utterance — but BaseWorker.is_voice_clone_consented matches
    /// against the route rows delivered by AUDIO_ROUTES_UPDATED, and with no row it fails closed.
    /// The late joiner's buffered audio was discarded and they permanently got a hashed default
    /// voice instead of their own. Voice cloning is this project's headline feature; a
    /// participant who joins a minute late silently loses it for the rest of the meeting.
    ///
    /// Incremental rather than a full GenerateRoutesAsync per join, deliberately. The mesh is
    /// O(n^2): regenerating it on every join makes the Nth joiner re-evaluate every existing
    /// pair, so a busy room pays O(n^3) route work over the course of filling up, and every one
    /// of those joins publishes a full AUDIO_ROUTES_UPDATED to every AI worker. This touches
    /// only the 2*(n-1) pairs that genuinely did not exist a moment ago.
    /// </summary>
    public async Task<Result<List<TranslationRoomAudioRouteDto>>> AddRoutesForParticipantAsync(Guid roomId, Guid participantId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            if (string.IsNullOrWhiteSpace(room.SourceLanguage) || string.IsNullOrWhiteSpace(room.TargetLanguages))
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorRoomPolicyIncomplete, ErrorCodes.InvalidState);
            }

            var participants = await _translationRoomParticipantRepository.GetByRoomIdAsync(roomId, ct);
            var joiner = participants?.FirstOrDefault(p => p.Id == participantId);
            if (joiner == null)
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
            }

            var existingRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var sourceLanguage = room.SourceLanguage;
            var targetLanguagesList = room.TargetLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var newRoutes = new List<TranslationRoomAudioRoute>();
            var updatedRoutes = new List<TranslationRoomAudioRoute>();

            // Only the pairs this participant is one half of: they speak to everyone already
            // here, and everyone already here speaks to them.
            foreach (var other in participants!.Where(p => p.Id != joiner.Id))
            {
                foreach (var (speaker, listener) in new[] { (joiner, other), (other, joiner) })
                {
                    var sourceLang = speaker.SpeakLanguage ?? sourceLanguage;
                    var targetLang = listener.ListenLanguage ?? targetLanguagesList.FirstOrDefault(l => l != sourceLang) ?? sourceLanguage;

                    if (!_languagePolicy.IsTranslationRequired(sourceLang, targetLang)) continue;

                    var existingRoute = existingRoutes.FirstOrDefault(r =>
                        r.SourceParticipantId == speaker.Id &&
                        r.TargetParticipantId == listener.Id);

                    if (existingRoute != null)
                    {
                        // A rejoin: the participant row is reused, so their route may already
                        // exist and may be carrying the languages they used last time.
                        if (existingRoute.SourceLanguage != sourceLang || existingRoute.TargetLanguage != targetLang)
                        {
                            existingRoute.SourceLanguage = sourceLang;
                            existingRoute.TargetLanguage = targetLang;
                            existingRoute.UpdatedAt = DateTime.UtcNow;
                            updatedRoutes.Add(existingRoute);
                        }
                        continue;
                    }

                    newRoutes.Add(new TranslationRoomAudioRoute
                    {
                        Id = Guid.NewGuid(),
                        TranslationRoomId = roomId,
                        SourceParticipantId = speaker.Id,
                        TargetParticipantId = listener.Id,
                        SourceLanguage = sourceLang,
                        TargetLanguage = targetLang,
                        VoiceCloneEnabled = SpeakerHasConsentedInRoom(existingRoutes, speaker.Id),
                        Status = AudioRouteStatus.PENDING.ToString(),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }

            if (newRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.AddRoutesAsync(newRoutes, ct);
            }

            if (updatedRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.UpdateRoutesAsync(updatedRoutes, ct);
            }

            if (newRoutes.Any() || updatedRoutes.Any())
            {
                await _unitOfWork.SaveChangesAsync(ct);
                // The AI workers' only source of route rows. Without this publish the rows exist
                // in Postgres and the consent gate still fails closed.
                await _audioRouteCacheService.PublishRoutesUpdateAsync(roomId, ct);

                _logger.LogInformation(
                    "Added {NewCount} and refreshed {UpdatedCount} audio routes for participant {ParticipantId} joining room {RoomId}",
                    newRoutes.Count, updatedRoutes.Count, participantId, roomId);
            }

            var allRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            return Result.Success(allRoutes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while adding audio routes for participant {ParticipantId} in room {RoomId}", participantId, roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Whether this speaker has already consented to voice cloning in THIS room.
    ///
    /// A new outgoing route for a speaker who already said yes inherits that yes. Consent is
    /// given per meeting and per speaker — SetVoiceCloneConsentAsync applies it to every route
    /// where the caller is the source, precisely because "a participant consents once for 'my
    /// voice may be cloned', not per listener". Defaulting the new route to false instead would
    /// mean an already-consented speaker silently drops back to a hashed default voice the
    /// moment anyone joins late, which is the same failure S7 is about, pointed the other way.
    ///
    /// This never widens consent: it can only copy a value the speaker themselves set, in this
    /// same room. A participant with no prior route (the late joiner as a speaker) still starts
    /// at false and must opt in.
    /// </summary>
    private static bool SpeakerHasConsentedInRoom(List<TranslationRoomAudioRoute> existingRoutes, Guid speakerParticipantId)
    {
        return existingRoutes.Any(r =>
            r.SourceParticipantId == speakerParticipantId &&
            r.Status != AudioRouteStatus.COMPLETED.ToString() &&
            r.VoiceCloneEnabled);
    }

    public async Task<Result<List<TranslationRoomAudioRouteDto>>> GetRoutesAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var routes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var dtos = routes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList();
            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching audio routing mesh for Room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomAudioRouteDto>> UpdateRuntimeContextAsync(Guid roomId, Guid routeId, UpdateAudioRouteRuntimeContextDto dto, CancellationToken ct = default)
    {
        try
        {
            var route = await _translationRoomAudioRouteRepository.GetByIdAsync(routeId, ct);
            if (route == null)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotFound, ErrorCodes.NotFound);
            }

            if (route.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotBelongToRoom, ErrorCodes.ValidationError);
            }

            if (route.Status == AudioRouteStatus.COMPLETED.ToString())
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorCannotUpdateCompletedRoute, ErrorCodes.InvalidState);
            }

            bool updated = false;
            if (dto.StreamId != null && route.StreamId != dto.StreamId)
            {
                route.StreamId = dto.StreamId;
                updated = true;
            }

            if (dto.Status != null && route.Status != dto.Status)
            {
                route.Status = dto.Status;
                if (dto.Status == AudioRouteStatus.BROADCASTING.ToString())
                {
                    route.StartedAt = DateTime.UtcNow;
                }
                updated = true;
            }

            if (updated)
            {
                route.UpdatedAt = DateTime.UtcNow;
                _translationRoomAudioRouteRepository.Update(route);
                await _unitOfWork.SaveChangesAsync(ct);

                await _audioRouteCacheService.PublishRoutesUpdateAsync(route.TranslationRoomId, ct);
            }

            return Result.Success(TranslationRoomAudioRouteMapper.ToDto(route));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating runtime context for route {RouteId}", routeId);
            return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomAudioRouteDto>> ToggleVoiceCloneAsync(Guid roomId, Guid routeId, ToggleVoiceCloneDto dto, CancellationToken ct = default)
    {
        try
        {
            var route = await _translationRoomAudioRouteRepository.GetByIdAsync(routeId, ct);
            if (route == null)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotFound, ErrorCodes.NotFound);
            }

            if (route.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotBelongToRoom, ErrorCodes.ValidationError);
            }

            if (route.Status == AudioRouteStatus.COMPLETED.ToString())
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorCannotUpdateCompletedRoute, ErrorCodes.InvalidState);
            }

            if (route.VoiceCloneEnabled != dto.VoiceCloneEnabled)
            {
                route.VoiceCloneEnabled = dto.VoiceCloneEnabled;
                route.UpdatedAt = DateTime.UtcNow;

                _translationRoomAudioRouteRepository.Update(route);
                await _unitOfWork.SaveChangesAsync(ct);

                await _audioRouteCacheService.PublishRoutesUpdateAsync(route.TranslationRoomId, ct);
            }

            return Result.Success(TranslationRoomAudioRouteMapper.ToDto(route));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while toggling voice clone for route {RouteId}", routeId);
            return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomAudioRouteDto>>> SetVoiceCloneConsentAsync(Guid roomId, Guid userId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var participant = await _translationRoomParticipantRepository.GetByRoomAndUserAsync(roomId, userId, ct);
            if (participant == null)
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
            }

            // Turning cloning ON requires a consent record in AuthService; turning it OFF never
            // does. Withdrawal must work even when the record cannot be read — the failure mode
            // of a consent system has to be "less processing", never "you are stuck consenting".
            //
            // This flag is what the AI pipeline reads (see base_worker.is_voice_clone_consented,
            // which fails closed on routes it has not received). Gating it here rather than in
            // the worker keeps the check off the per-utterance hot path: the pipeline still
            // consults exactly one local field, and that field can now only be true for somebody
            // who agreed to it in a record that outlives this meeting.
            if (enabled && !await _voiceConsentDirectory.HasVoiceCloneConsentAsync(userId, ct))
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(
                    AudioRouteConstants.ErrorVoiceCloneConsentMissing, ErrorCodes.Forbidden);
            }

            var allRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            // Every route where THIS caller is the speaker — a participant consents once
            // for "my voice may be cloned", not per listener. A listener who joins later gets
            // a route from AddRoutesForParticipantAsync that INHERITS this speaker's answer
            // (see SpeakerHasConsentedInRoom): consent is per speaker and per meeting, so a
            // speaker who already said yes must not silently drop back to a default voice
            // just because somebody new walked in. It is never inherited across meetings, and
            // the joiner's own outgoing routes still start at false.
            var myOutgoingRoutes = allRoutes
                .Where(r => r.SourceParticipantId == participant.Id
                    && r.Status != AudioRouteStatus.COMPLETED.ToString())
                .ToList();

            var changedRoutes = myOutgoingRoutes.Where(r => r.VoiceCloneEnabled != enabled).ToList();
            if (changedRoutes.Any())
            {
                foreach (var route in changedRoutes)
                {
                    route.VoiceCloneEnabled = enabled;
                    route.UpdatedAt = DateTime.UtcNow;
                }

                await _translationRoomAudioRouteRepository.UpdateRoutesAsync(changedRoutes, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                await _audioRouteCacheService.PublishRoutesUpdateAsync(roomId, ct);
            }

            var dtos = myOutgoingRoutes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList();
            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while setting voice clone consent for user {UserId} in room {RoomId}", userId, roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }
}
